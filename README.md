# BrianMed.SmartCrop

This is a .Net 6 and ImageSharp port of softaware gmbh's .Net Standard port of Jonas Wagner's [smartcrop.js](https://github.com/jwagner/smartcrop.js) content aware image cropping library. 

## Usage

Install the nuget package BrianMed.SmartCrop

Add the following code:

```csharp
using (var image = File.OpenRead(args[0]))
{
    // find best crop
    var result = new ImageCrop(200, 200).Crop(image);

    // crop the image
    using (var crop = Image.Load<Rgba32>(args[0]))
    {
        crop.Mutate(ctx =>
        {
            ctx.Crop(result.Area);
        });

        crop.SaveAsPng(args[1]);
    }

    Console.WriteLine(
        $"Best crop: {result.Area.X}, {result.Area.Y} - {result.Area.Width} x {result.Area.Height}");
}
```

## Notes

There are several opportunities for improvement.  Patches are welcome.
