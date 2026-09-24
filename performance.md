# DirectX 操作与性能路径

本文记录当前实现中 `Update`、`UpdateTile` 和 `Load` 的 Direct3D 11 操作、临时资源和数据方向。tile 纹理、shader、sampler 和 constant buffer 随所属 `GrotheImage` 或 `LayerCompose` 存活；调用过程创建的 source、render target 和 staging texture 在调用结束时释放。

## 设备和长期资源

第一次需要 GPU 的操作会创建一个 D3D11 device/context。每个 layer 按 tile 顺序分配 `Texture2DArray` page，page 最多包含 2048 个 tile slice。普通格式使用一个 texture array；`Yuv444`、`Yuv422` 和 `Yuv420` 使用两个 texture array：

- `Y` 是 `R8_UNorm`。
- `UV` 是 `R8G8_UNorm`。
- `Yuv422` 的 UV 尺寸是 tile 的 `width / 2 x height`。
- `Yuv420` 的 UV 尺寸是 tile 的 `width / 2 x height / 2`。

这些 tile 纹理不会在每次 API 调用后释放，只有 `GrotheImage.Dispose` 时释放。

## Update

`Update` 的输入是用户内存中的一张图和 source-to-image 的透视矩阵：

1. 根据本次 source `PixelFormat` 创建 GPU source texture：`Gray8` 使用 `R8_UNorm`，`Bgra32` 使用 `B8G8R8A8_UNorm`，`Rgba32` 使用 `R8G8B8A8_UNorm`。其他格式作为输入会被拒绝。
2. 将输入内存通过 `UpdateSubresource` 上传到 source texture。这个上传只发生一次，之后的格式转换、透视采样和写入全部由 compute shader 完成。
3. 根据透视矩阵包围盒，只遍历可能被源图覆盖的 tile。
4. 一个图像级 `UpdateProgram` 缓存 compute shader、sampler 和 constant buffer，不按输入格式重新编译。shader 直接读取采样值；`Gray8` 采样结果的 G/B 为 0，不扩展为灰色 RGB。shader 根据 tile 原点反算 source 坐标，按目标存储格式写入 tile UAV。YUV 目标同时写 Y 和 UV 两个 UAV；YUV420/422 只在对应的偶数像素写 UV。
5. `Flush` 后释放本次 source texture/view；program 随图像释放。

没有 CPU readback，也不会为每个 tile 建立 render target。目标 tile 直接作为 UAV 写入，避免了 render-target 到 texture 的额外 `CopyResource`。

## UpdateTile

`UpdateTile` 的输入尺寸必须等于当前 `GrotheImageInfo.TileWidth/TileHeight`，输入格式仅可为 `Bgra32`、`Rgba32` 或 `Gray8`。目标 `GrotheImage` 仍可采用 YUV 存储格式。

### 格式相同且可直接上传

以下路径直接对目标 tile 的 texture array slice 调用 `UpdateSubresource`：

- `Bgra32 -> Bgra32`
- `Rgba32 -> Rgba32`
- `Gray8 -> Gray8`

此路径没有中间 texture、shader 或 readback，是 tile 更新的最低开销路径。

### 格式不同或需要布局转换

其他情况使用 `UpdateProgram.ExecuteTile`：

1. 输入上传为一个只读 source texture，不生成 CPU 转换缓冲。
2. 使用图像已缓存的 compute shader 以 tile 的目标坐标采样 source。shader 完成颜色到存储平面的转换和 YUV 子采样。
3. 目标 tile 的 color UAV 或 Y/UV UAV 直接接收结果。

因此例如 `Bgra32 -> Yuv420` 不会把 BGRA 在 CPU 转成 Y/UV，也不会直接把 packed 数据写进 YUV texture；GPU shader 会生成 Y plane 和半分辨率 UV plane。公开 API 不接受 YUV 格式的源缓冲区。

## Load

`Load` 的矩阵表示 source image 到 output image 的变换。实现内部先求逆矩阵，pixel shader 根据当前输出像素反算 GrotheImage 坐标，再选择 page、array slice 和 tile-local 坐标。

1. 创建 output render target texture，格式由输出 `PixelFormat` 决定。
2. 创建同格式 staging texture。普通 Load 使用图像级缓存的 vertex/pixel shader、sampler 和 constant buffer；Compose Load 使用对应 `LayerCompose` 缓存的 program。
3. 对覆盖输出区域的每个 page 绑定 texture-array SRV。layer load 只绑定一个 layer；compose load 按 layer 顺序绑定所有 layer 的 SRV。
4. 绘制一个 full-screen triangle。pixel shader 完成透视反算、tile 选择、overlap 采样规则、双线性采样、compose 数学表达式和输出格式转换。
5. 使用 `CopyResource(staging, render)`，`Map(Read)` staging texture，并把行数据复制到调用者提供的 `scan0/stride`。
6. 释放本次输出的 render/staging texture。普通 Load program 随 `GrotheImage.Dispose()` 释放，Compose Load program 随 `LayerCompose.Dispose()` 或所属图像释放。

Load 必须把 GPU 结果交给用户内存，所以 staging map 和 CPU 行复制是必要的；除此之外，采样和计算都在 GPU 上完成。Playground 的 PNG/JPEG 导出只在 Load 完成后把输出字节交给 WPF encoder，编码不属于 GrotheImage 的 DX 路径。

## BlendSeams

`BlendSeams` 把每个内部接缝的重叠带混合后写回两个 tile，全程只在 GPU 上做原地修改：

1. 按 layer、平面（YUV 图像是 luma 和 chroma 两个平面）、接缝三重循环枚举，每个接缝一次 compute dispatch；线程数等于重叠带像素数（带宽 = overlap）。
2. 一个线程读取两个 tile 在同一带内偏移上的两个 texel，算出混合值后**同时写回这两个 texel**。写回的两份完全相同，所以整幅图像只存一份等价数据，重复调用不会继续改变像素。
3. 相邻两个 tile 落在同一个 page texture 时只绑定一个 UAV（用 slice 区分）；跨越 page 边界时用两个 UAV 绑定两个 page texture，两种情形各有一个 shader 变体。
4. 相邻两个 tile 只要有一个没写过就跳过这个接缝，避免把空 tile 的零值抹到邻居身上；`TileOverlapX`/`TileOverlapY` 为 0 的方向没有接缝，直接跳过。

成本是 `(列数-1)*行数 + (行数-1)*列数` 次左右的 dispatch（乘 layer 数和平面数），每次 dispatch 的线程数只有重叠带那么大；没有 CPU 回读、没有临时纹理、没有额外的全图 pass。它比 `Update`/`Load` 次数多但每次都很小，属于"填充完成后一次性"的开销。

## 内存与速度注意事项

- tile array page 是主要常驻显存；未使用的 page 不会因为 Load 自动创建以外的调用而全部分配。
- `UpdateTile` 的转换中间资源只包含一个 tile，避免为整幅超大图像分配转换缓冲。
- 直接格式匹配时跳过 shader 和中间资源。
- 24 位和 YUV 传输格式会在参数校验时拒绝；Playground 使用 WPF 解码器将这类输入图像转换为 BGRA32。
- 每个 `GrotheImage` 最多创建一个普通 Load program、一个 Update program 和一个 Blend program；每个被实际加载的 `LayerCompose` 最多创建一个自己的 Load program，重复调用时只创建单次传输纹理。
- `Shaders/` 下的 HLSL 以 embedded resource 形式随程序集发布，进程内只读取和解析一次（成员重载表）；`CreateLayerCompose` 只做签名匹配，`Load` 不重复解析。C# 侧不拼接 shader 源码，只生成一个 `macros` include：`LAYER_COUNT`、`STORAGE_*`、`MEMBER_LIBRARY` 和 `COMPOSE_PIXEL` 都在其中，shader 用 `#if` 选择分支。只有表达式真的用到成员时 `MEMBER_LIBRARY` 才会被定义，`Common.hlsl` 也才会被 include 进这次编译。
