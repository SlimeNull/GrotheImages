

// color
float4 color(float value)
{
    return float4(value, value, value, 1);
}
float4 color(float2 value)
{
    return float4(value, 0, 1);
}
float4 color(float3 value)
{
    return float4(value.xyz, 1);
}
float4 color(float4 value)
{
    return value;
}

// lum
float lum(float v)
{
    return v;
}
float lum(float3 v)
{
    return v.r * 0.299 + v.g * 0.587 + v.b * 0.114;
}
float lum(float2 v)
{
    return lum(float3(v.r, v.g, 0));
}
float lum(float4 v)
{
    return lum(v.rgb) * v.a;
}
float3 sharpen(float3 v)
{
    return v;
}

// distance
float distance(float v)
{
    return distance(v, 0);
}
float distance(float2 v)
{
    return distance(v, 0);
}
float distance(float3 v)
{
    return distance(v, 0);
}
float distance(float4 v)
{
    return distance(v.rgb, 0);
}

// hhh
float3 hhh(float3 v)
{
    return abs(float3(v.r - v.g, v.g - v.b, v.b - v.r));
}
float4 hhh(float4 v)
{
    v.rgb = hhh(v.rgb);
    return v;
}

// bin
float bin(float v, float threshold)
{
    return step(threshold, v);
}
float2 bin(float2 v, float2 threshold)
{
    return step(threshold, v);
}
float3 bin(float3 v, float3 threshold)
{
    return step(threshold, v);
}
float4 bin(float4 v, float4 threshold)
{
    return step(threshold, v);
}

// bin2
float bin2(float v, float lo, float hi)
{
    float diff1 = v - lo;
    float diff2 = hi - v;
    return step(0, diff1 * diff2);
}
float2 bin2(float2 v, float2 lo, float2 hi)
{
    float2 diff1 = v - lo;
    float2 diff2 = hi - v;
    return step(0, diff1 * diff2);
}
float3 bin2(float3 v, float3 lo, float3 hi)
{
    float3 diff1 = v - lo;
    float3 diff2 = hi - v;
    return step(0, diff1 * diff2);
}
float4 bin2(float4 v, float4 lo, float4 hi)
{
    float4 diff1 = v - lo;
    float4 diff2 = hi - v;
    return step(0, diff1 * diff2);
}

// hsv
float3 hsv(float3 color)
{
    float r, g, b, delta;
    float colorMax, colorMin;
    float h = 0, s = 0, v = 0;
	
    r = color.r;
    g = color.g;
    b = color.b;
	
    colorMax = max(r, g);
    colorMax = max(colorMax, b);
    colorMin = min(r, g);
    colorMin = min(colorMin, b);
	
    v = colorMax; // this is value
    delta = colorMax - colorMin;
	
    if (delta != 0)
    {
        s = delta / colorMax;
		
        if (r == colorMax)
        {
            h = (g - b) / delta;
        }
        else if (g == colorMax)
        {
            h = 2.0 + (b - r) / delta;
        }
        else // b is max
        {
            h = 4.0 + (r - g) / delta;
        }
        h /= 6.0;
        if (h < 0)
        {
            h += 1.0;
        }
    }
	
    float3 hsv;
    hsv.rgb = float3(h, s, v);
    return hsv;
}

float4 hsv(float4 color)
{
    return float4(hsv(color.rgb), color.a);
}

// min
float min(float v)
{
    return v;
}
float min(float2 v)
{
    return min(v.r, v.g);
}
float min(float3 v)
{
    return min(min(v.rg), v.b);
}
float min(float4 v)
{
    return min(min(v.rgb), v.a);
}

// max
float max(float v)
{
    return v;
}
float max(float2 v)
{
    return max(v.r, v.g);
}
float max(float3 v)
{
    return max(max(v.rg), v.b);
}
float max(float4 v)
{
    return max(max(v.rgb), v.a);
}

// sort
float sort(float v)
{
    return v;
}
float2 sort(float2 v)
{
    return (v.r > v.g) ? v.gr : v;
}
float3 sort(float3 v)
{
    v.rg = sort(v.rg);
    return v.b < v.r ? v.brg : (v.b < v.g ? v.rbg : v.rgb);
}
float4 sort(float4 v)
{
    v.rgb = sort(v.rgb);
    return v.a < v.r ? v.argb : (v.a < v.g ? v.ragb : (v.a < v.b ? v.rgab : v.rgba));
}

// product
float product(float v)
{
    return v;
}
float product(float2 v)
{
    return v.r * v.g;
}
float product(float3 v)
{
    return v.r * v.g * v.b;
}
float product(float4 v)
{
    return v.r * v.g * v.b * v.a;
}

// sum
float sum(float v)
{
    return v;
}
float sum(float2 v)
{
    return v.r + v.g;
}
float sum(float3 v)
{
    return v.r + v.g + v.b;
}
float sum(float4 v)
{
    return v.r + v.g + v.b + v.a;
}

// avg
float avg(float v)
{
    return v;
}
float avg(float2 v)
{
    return (v.r + v.g) / 2;
}
float avg(float3 v)
{
    return (v.r + v.g + v.b) / 3;
}
float avg(float4 v)
{
    return (v.r + v.g + v.b + v.a) / 4;
}

// rgb_to_xyz D65
float3 rgb_to_xyz(float3 rgb)
{
    float r = rgb.r;
    float g = rgb.g;
    float b = rgb.b;

    float X = 0.412453 * r + 0.357580 * g + 0.180423 * b;
    float Y = 0.212671 * r + 0.715160 * g + 0.072169 * b;
    float Z = 0.019334 * r + 0.119193 * g + 0.950227 * b;

    return float3(X, Y, Z);
}
float4 rgb_to_xyz(float4 rgba)
{
    return float4(rgba.rgb, rgba.a);
}

// xyz_to_rgb D65
float3 xyz_to_rgb(float3 xyz)
{
    float x = xyz.x;
    float y = xyz.y;
    float z = xyz.z;

    float r = 3.240479 * x - 1.537150 * y - 0.498535 * z;
    float g = -0.969256 * x + 1.875992 * y + 0.041556 * z;
    float b = 0.055648 * x - 0.204043 * y + 1.057311 * z;

    return float3(r, g, b);
}

float4 xyz_to_rgb(float4 xyzw)
{
    return float4(xyzw.xyz, xyzw.w);
}

// xyz_to_luv
float3 xyz_to_luv(float3 xyz)
{
    float Y = xyz.y;
    float X = xyz.x;
    float Z = xyz.z;

    float Yn = 1.0;
    float xn = 0.312713;
    float yn = 0.329016;
    float un = 4 * xn / (-2 * xn + 12 * yn + 3);
    float vn = 9 * yn / (-2 * xn + 12 * yn + 3);

    float L;
    if ((Y / Yn) > pow(6.0 / 29.0, 3))
    {
        L = 116.0 * pow(Y / Yn, 1.0 / 3.0) - 16.0;
    }
    else
    {
        L = pow(29.0 / 3.0, 3) * (Y / Yn);
    }
    float u = 4 * X / (X + 15 * Y + 3 * Z);
    float v = 9 * Y / (X + 15 * Y + 3 * Z);
    float U = 13 * L * (u - un);
    float V = 13 * L * (v - vn);

    return float3(L, U, V);
}


float4 xyz_to_luv(float4 xyzw)
{
    return float4(xyz_to_luv(xyzw.xyz), xyzw.w);
}

// luv_to_xyz
float3 luv_to_xyz(float3 luv)
{
    float L = luv.x;
    float U = luv.y;
    float V = luv.z;

    float Yn = 1.0;
    float xn = 0.312713;
    float yn = 0.329016;
    float un = 4 * xn / (-2 * xn + 12 * yn + 3);
    float vn = 9 * yn / (-2 * xn + 12 * yn + 3);

    float u = U / (13 * L) + un;
    float v = V / (13 * L) + vn;

    float Y;
    if (L > 8)
    {
        Y = Yn * pow((L + 16) / 116.0, 3);
    }
    else
    {
        Y = Yn * L * pow(3.0 / 29.0, 3);
    }

    float X = Y * 9.0 * u / (4.0 * v);
    float Z = Y * (12 - 3 * u - 20 * v) / (4.0 * v);

    return float3(X, Y, Z);
}

float4 luv_to_xyz(float4 luv)
{
    return float4(luv_to_xyz(luv.xyz), luv.a);
}

// rgb_to_luv
float3 rgb_to_luv(float3 rgb)
{
    return xyz_to_luv(rgb_to_xyz(rgb));
}
float4 rgb_to_luv(float4 rgba)
{
    return xyz_to_luv(rgb_to_xyz(rgba));
}

// luv_to_rgb
float3 luv_to_rgb(float3 luv)
{
    return xyz_to_rgb(luv_to_xyz(luv));
}
float4 luv_to_rgb(float4 luv)
{
    return xyz_to_rgb(luv_to_xyz(luv));
}

// mul_rgb_to_a
float4 mul_rgb_to_a(float3 v)
{
    return float4(v.rgb, v.r * v.g * v.b);
}
float4 mul_rgb_to_a(float4 v)
{
    return mul_rgb_to_a(v.rgb);
}