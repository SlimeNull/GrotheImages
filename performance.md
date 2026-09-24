# DirectX 操作与性能路径

本文记录当前实现中 `Update`、`UpdateTile` 和 `Load` 的 Direct3D 11 操作、临时资源和数据方向。`GrotheImage` 的 tile 纹理和 texture array page 是长期资源；调用过程创建的 source、shader、constant buffer、render target 和 staging resource 都在调用结束时释放。

## 设备和长期资源

第一次需要 GPU 的操作会创建一个 D3D11 device/context。每个 layer 按 tile 顺序分配 `Texture2DArray` page，page 最多包含 2048 个 tile slice。普通格式使用一个 texture array；`Yuv444`、`Yuv422` 和 `Yuv420` 使用两个 texture array：

- `Y` 是 `R8_UNorm`。
- `UV` 是 `R8G8_UNorm`。
- `Yuv422` 的 UV 尺寸是 tile 的 `width / 2 x height`。
- `Yuv420` 的 UV 尺寸是 tile 的 `width / 2 x height / 2`。

这些 tile 纹理不会在每次 API 调用后释放，只有 `GrotheImage.Dispose` 时释放。

## Update

`Update` 的输入是用户内存中的一张图和 source-to-image 的透视矩阵：

1. 按 source `PixelFormat` 创建一个 GPU source texture。`Gray8` 使用 `R8_UNorm`，其他 packed 输入使用 RGBA texture；BGR/RGB 24 位输入在上传前扩展为四通道，因为 D3D11 没有可直接采样的 24 位 typed texture。
2. 将输入内存通过 `UpdateSubresource` 上传到 source texture。这个上传只发生一次，之后的格式转换、透视采样和写入全部由 compute shader 完成。
3. 根据透视矩阵包围盒，只遍历可能被源图覆盖的 tile。
4. compute shader 对每个目标 tile 执行双线性采样。shader 根据 tile 原点把目标坐标反算为 source 坐标，转换 RGB/YUV，并写入目标 tile 的 UAV。YUV 目标同时写 Y 和 UV 两个 UAV；YUV420/422 只在对应的偶数像素写 UV。
5. `Flush` 后释放 source texture/view、sampler、shader 和 constant buffer。

没有 CPU readback，也不会为每个 tile 建立 render target。目标 tile 直接作为 UAV 写入，避免了 render-target 到 texture 的额外 `CopyResource`。

## UpdateTile

`UpdateTile` 的输入尺寸必须等于当前 `GrotheImageInfo.TileWidth/TileHeight`，但输入格式可以和 `GrotheImage.Format` 不同。

### 格式相同且可直接上传

以下路径直接对目标 tile 的 texture array slice 调用 `UpdateSubresource`：

- `Bgra32 -> Bgra32`
- `Rgba32 -> Rgba32`
- `Gray8 -> Gray8`
- 相同的 YUV 平面格式（Y 和 UV 分别上传）

此路径没有中间 texture、shader 或 readback，是 tile 更新的最低开销路径。

### 格式不同或需要布局转换

其他情况使用 `UpdateProgram.ExecuteTile`：

1. packed 输入先上传为一个只读 source texture；YUV422/YUV420 平面输入分别上传为 Y source SRV 和 UV source SRV。平面输入不需要在 CPU 上重新打包。
2. 使用 compute shader 以 tile 的目标坐标为输出坐标采样 source。shader 完成通道顺序转换、RGB/Gray/YUV 转换和 YUV 子采样。
3. 目标 tile 的 color UAV 或 Y/UV UAV 直接接收结果。

因此例如 `Bgra32 -> Yuv420` 不会把 BGRA 在 CPU 转成 Y/UV，也不会直接把 packed 数据写进 YUV texture；它会通过 GPU shader 生成 Y plane 和半分辨率 UV plane。`Yuv420 -> Bgra32` 也走同一个 GPU 转换方向。

## Load

`Load` 的矩阵表示 source image 到 output image 的变换。实现内部先求逆矩阵，pixel shader 根据当前输出像素反算 GrotheImage 坐标，再选择 page、array slice 和 tile-local 坐标。

1. 创建 output render target texture，格式由输出 `PixelFormat` 决定。
2. 创建同格式 staging texture，并创建一次本次操作使用的 vertex shader、pixel shader、linear sampler 和 constant buffer。
3. 对覆盖输出区域的每个 page 绑定 texture-array SRV。layer load 只绑定一个 layer；compose load 按 layer 顺序绑定所有 layer 的 SRV。
4. 绘制一个 full-screen triangle。pixel shader 完成透视反算、tile 选择、overlap 采样规则、双线性采样、compose 数学表达式和输出格式转换。
5. 使用 `CopyResource(staging, render)`，`Map(Read)` staging texture，并把行数据复制到调用者提供的 `scan0/stride`。
6. 释放本次输出的 render/staging texture 和 shader 资源。

Load 必须把 GPU 结果交给用户内存，所以 staging map 和 CPU 行复制是必要的；除此之外，采样和计算都在 GPU 上完成。Playground 的 PNG/JPEG 导出只在 Load 完成后把输出字节交给 WPF encoder，编码不属于 GrotheImage 的 DX 路径。

## 内存与速度注意事项

- tile array page 是主要常驻显存；未使用的 page 不会因为 Load 自动创建以外的调用而全部分配。
- `UpdateTile` 的转换中间资源只包含一个 tile，避免为整幅超大图像分配转换缓冲。
- 直接格式匹配时跳过 shader 和中间资源。
- 24 位 packed 输入需要一次 CPU 扩展才能上传到可采样的 D3D11 texture；扩展后的数组在 shader 创建前释放。
- 当前 shader/pipeline 对象按一次 API 调用创建并释放。后续若批量更新大量 tile，可在同一 D3D11 device 上缓存按 source/target format 分组的 shader、sampler 和 constant-buffer 描述，以减少编译和对象创建开销；tile 数据和 page 生命周期无需改变。
