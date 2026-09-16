// Растеризация знака из вектора. Инструмент разработки, в поставку не идёт.
//
// Каждый размер рисуется из ИСХОДНОГО вектора, а не уменьшением большого
// растра: при уменьшении тонкие линии и свечение превращаются в кашу.
//
// Векторов три, по ступеням размеров. Это не украшательство: свечение,
// обводка и мелкие детали заданы в единицах холста 512, и на 16 px обводка в
// три единицы — это девять сотых пикселя, то есть её нет, а размытие в восемь
// единиц накрывает знак целиком. Один вектор на все размеры даёт либо пустой
// силуэт внизу, либо голую плашку вверху.

using SkiaSharp;
using Svg.Skia;

// Одиночный вектор в PNG — чтобы смотреть правки крупно, не собирая иконку.
if (args.Length >= 2 && args[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
{
    var one = new SKSvg();
    if (one.Load(args[0]) is null)
    {
        Console.WriteLine($"не разобрал {args[0]}");
        return 1;
    }

    // Знак трея — один вектор на все размеры, в отличие от знака приложения:
    // он мелкий по назначению, ступеней ему не нужно, а рисуется всё равно
    // каждый размер из вектора.
    if (args[1].EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
    {
        int[] traySizes = [16, 20, 24, 32, 40, 48, 64];

        WriteIco(args[1], traySizes
            .Select(size => (size, Render(one.Picture!, one.Picture!.CullRect, size)))
            .ToList());

        Console.WriteLine($"иконка: {args[1]}, кадров {traySizes.Length}, {new FileInfo(args[1]).Length} Б");

        return 0;
    }

    var side = args.Length > 2 ? int.Parse(args[2]) : 256;
    File.WriteAllBytes(args[1], Render(one.Picture!, one.Picture!.CullRect, side));
    Console.WriteLine($"{args[1]}: {side}×{side}");

    return 0;
}

var dir = args.Length > 0 ? args[0] : ".";
var target = args.Length > 1 ? args[1] : "app.ico";
var sheet = args.Length > 2 ? args[2] : null;

// Ступени: до какого размера включительно годится этот вектор.
(int Upto, string File)[] steps =
[
    (24, "icon-small.svg"),
    (64, "icon-medium.svg"),
    (512, "icon.svg"),
];

var pictures = new Dictionary<string, (SKPicture Picture, SKRect Bounds)>();

foreach (var (_, file) in steps)
{
    var path = Path.Combine(dir, file);
    var svg = new SKSvg();

    if (svg.Load(path) is null)
    {
        Console.WriteLine($"не разобрал {path}");
        return 1;
    }

    pictures[file] = (svg.Picture!, svg.Picture!.CullRect);
    Console.WriteLine($"{file}: {svg.Picture!.CullRect.Width:F0}×{svg.Picture!.CullRect.Height:F0}");
}

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = new List<(int Size, byte[] Png)>();

foreach (var size in sizes)
{
    var step = steps.First(s => size <= s.Upto);
    var (picture, bounds) = pictures[step.File];

    frames.Add((size, Render(picture, bounds, size)));
}

WriteIco(target, frames);
Console.WriteLine($"иконка: {target}, кадров {frames.Count}, {new FileInfo(target).Length} Б");

// Полоса размеров на тёмном и на светлом: панель задач бывает и той и другой,
// а знак, читаемый только на одном фоне, — это знак, который половина людей
// не разглядит.
if (sheet is not null)
{
    Sheet(sizes, steps, pictures, sheet);
    Console.WriteLine($"полоса: {sheet}");
}

return 0;

static byte[] Render(SKPicture picture, SKRect bounds, int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);

    var scale = size / Math.Max(bounds.Width, bounds.Height);
    canvas.Scale(scale);
    canvas.Translate(-bounds.Left, -bounds.Top);
    canvas.DrawPicture(picture);
    canvas.Flush();

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);

    return data.ToArray();
}

static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
{
    using var stream = File.Create(path);
    using var w = new BinaryWriter(stream);

    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)frames.Count);

    var offset = 6 + 16 * frames.Count;

    foreach (var (size, png) in frames)
    {
        // 256 пишется нулём: под размер отведён один байт.
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write((uint)png.Length);
        w.Write((uint)offset);

        offset += png.Length;
    }

    foreach (var (_, png) in frames) w.Write(png);
}

static void Sheet(
    int[] sizes,
    (int Upto, string File)[] steps,
    Dictionary<string, (SKPicture Picture, SKRect Bounds)> pictures,
    string path)
{
    int[] shown = [16, 24, 32, 48, 64, 128];
    const int pad = 14;

    var width = shown.Sum() + pad * (shown.Length + 1);
    var height = 128 + pad * 2;

    using var surface = SKSurface.Create(new SKImageInfo(width, height * 2));
    var canvas = surface.Canvas;

    canvas.Clear(new SKColor(0x18, 0x18, 0x18));
    using (var light = new SKPaint { Color = new SKColor(0xEC, 0xEC, 0xEC) })
        canvas.DrawRect(0, height, width, height, light);

    var x = pad;

    foreach (var size in shown)
    {
        var step = steps.First(s => size <= s.Upto);
        var (picture, bounds) = pictures[step.File];

        using var bitmap = SKBitmap.Decode(Render(picture, bounds, size));

        var y = (height - size) / 2;
        canvas.DrawBitmap(bitmap, x, y);
        canvas.DrawBitmap(bitmap, x, height + y);

        x += size + pad;
    }

    canvas.Flush();

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var file = File.Create(path);

    data.SaveTo(file);
}
