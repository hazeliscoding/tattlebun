#:package Svg.Skia@5.2.3
#:property ManagePackageVersionsCentrally=false

// Renders docs/brand/mark.svg on a paper tile into the multi-size .ico used by the tray, the exe
// and Setup. The tile keeps the ink mark visible on a dark taskbar.
// Run from the repo root: dotnet run scripts/make-icons.cs
using SkiaSharp;
using Svg.Skia;

var brand = Path.Combine("docs", "brand");
using var svg = new SKSvg();
svg.Load(Path.Combine(brand, "mark.svg"));
var mark = svg.Picture ?? throw new InvalidOperationException("mark.svg did not render");

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = sizes.Select(size => Render(mark, size)).ToList();

using var ico = File.Create(Path.Combine(brand, "tattlebun.ico"));
using var writer = new BinaryWriter(ico);
writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)sizes.Length);
var offset = 6 + 16 * sizes.Length;
for (var i = 0; i < sizes.Length; i++)
{
    // A 256 px entry is stored as 0 in the one-byte width and height fields.
    writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
    writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(pngs[i].Length);
    writer.Write(offset);
    offset += pngs[i].Length;
}
foreach (var png in pngs)
{
    writer.Write(png);
}

Console.WriteLine($"Wrote tattlebun.ico ({string.Join(", ", sizes)})");

static byte[] Render(SKPicture mark, int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / 64f);
    using var paper = new SKPaint { Color = SKColor.Parse("#f2ede2"), IsAntialias = true };
    canvas.DrawRoundRect(0, 0, 64, 64, 14, 14, paper);
    canvas.Translate(3.2f, 3.2f);
    canvas.Scale(0.9f * 64 / mark.CullRect.Width);
    canvas.DrawPicture(mark);
    canvas.Flush();
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}
