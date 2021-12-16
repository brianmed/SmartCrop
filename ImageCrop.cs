using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BrianMed.SmartCrop
{
    public class ImageCrop
    {
        private Options options;

        public ImageCrop(int width, int height)
            : this(new Options(width, height))
        {
        }

        public ImageCrop(Options options)
        {
            this.Options = options;
        }

        public Options Options
        {
            get => this.options;
            set => this.options = value ?? throw new ArgumentNullException(nameof(value));
        }

        public Result Crop(byte[] imageBytes, params BoostArea[] boostAreas)
        {
            using (var image = Image.Load<Rgba32>(imageBytes))
            {
                return this.Crop(image, boostAreas);
            }
        }

        public Result Crop(Stream imageStream, params BoostArea[] boostAreas)
        {
            using (var image = Image.Load<Rgba32>(imageStream))
            {
                return this.Crop(image, boostAreas);
            }
        }

        public Result Crop(Image<Rgba32> image, params BoostArea[] boostAreas)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }

            Image<Rgba32> resizedImage = image.Clone();

            try
            {
                if (this.Options.Aspect > 0)
                {
                    this.Options.Width = this.Options.Aspect;
                    this.Options.Height = 1;
                }

                var scale = 1f;
                var prescale = 1f;

                if (this.Options.Width > 0 && this.Options.Height > 0)
                {
                    scale = Math.Min(
                        image.Width / (float)this.Options.Width,
                        image.Height / (float)this.Options.Height
                    );
                    this.Options.CropWidth = (int)Math.Round(this.Options.Width * scale);
                    this.Options.CropHeight = (int)Math.Round(this.Options.Height * scale);

                    // Img = 100x100; width = 95x95; scale = 100/95; 1/scale > min
                    // don't set minScale smaller than 1/scale
                    // -> don't pick crops that need upscaling
                    this.Options.MinScale = Math.Min(
                        this.Options.MaxScale,
                        Math.Max(1.0f / scale, this.Options.MinScale)
                    );

                    // prescale if possible
                    if (this.Options.Prescale)
                    {
                        prescale = Math.Min(Math.Max(256f / image.Width, 256f / image.Height), 1);
                        if (prescale < 1)
                        {
                            resizedImage.Mutate(ctx =>
                            {
                                int width = (int)Math.Round(image.Width * prescale);
                                int height = (int)Math.Round(image.Height * prescale);

                                ctx.Resize(width, height);
                            });


                            this.Options.CropWidth = (int)Math.Round(this.Options.CropWidth.Value * (double)prescale);
                            this.Options.CropHeight = (int)Math.Round(this.Options.CropHeight.Value * (double)prescale);

                            for (int i = 0; i < boostAreas.Length; i++)
                            {
                                var area = boostAreas[i].Area;
                                boostAreas[i].Area =
                                    new Rectangle(
                                        (int)Math.Round(area.X * prescale),
                                        (int)Math.Round(area.Y * prescale),
                                        (int)Math.Round(area.Width * prescale),
                                        (int)Math.Round(area.Height * prescale));
                            }
                        }
                        else
                        {
                            prescale = 1;
                        }
                    }
                }

                var result = this.Analyze(resizedImage ?? image, boostAreas);

                if (this.Options.Prescale)
                {
                    result.Area = new Rectangle(
                    (int)Math.Round(result.Area.X / prescale),
                    (int)Math.Round(result.Area.Y / prescale),
                    (int)Math.Round(result.Area.Width / prescale),
                    (int)Math.Round(result.Area.Height / prescale));
                }

                return result;
            }
            finally
            {
                resizedImage?.Dispose();
            }
        }

        private Result Analyze(Image<Rgba32> input, params BoostArea[] boostAreas)
        {
            using (var output = new Image<Rgba32>(input.Width, input.Height))
            {
                var result = new Result();

                this.EdgeDetect(input, output);
                this.SkinDetect(input, output);
                this.SaturationDetect(input, output);
                this.ApplyBoosts(output, boostAreas);

                using (var scoreOutput = this.DownSample(output))
                {
                    var topScore = double.MinValue;
                    var crops = this.GenerateCrops(input.Width, input.Height);

                    foreach (var crop in crops)
                    {
                        crop.Score = this.Score(scoreOutput, crop.Area, boostAreas);
                        if (crop.Score.Total > topScore)
                        {
                            result.Area = crop.Area;
                            topScore = crop.Score.Total;
                        }
                    }

                    return result;
                }
            }
        }

        private void EdgeDetect(Image<Rgba32> input, Image<Rgba32> output)
        {
            var w = input.Width;
            var h = input.Height;

            byte[] bytesInput;

            if (input.TryGetSinglePixelSpan(out var pixelSpan)) {
                bytesInput = MemoryMarshal.AsBytes(pixelSpan).ToArray();
            } else {
                throw new Exception("Issue with memory");
            }

            int idx = 0;

            for (var y = 0; y < h; y++)
            {
                Span<Rgba32> pixelsInput = input.GetPixelRowSpan(y);
                Span<Rgba32> pixelsOutput = output.GetPixelRowSpan(y);

                for (var x = 0; x < w; x++)
                {
                    float lightness;

                    if (x == 0 || x >= w - 1 || y == 0 || y >= h - 1)
                    {
                        lightness = this.Sample(pixelsInput[x]);
                    }
                    else
                    {
                        int byteIdx = idx;

                        Rgba32 current = new(
                            bytesInput[(byteIdx+0)],
                            bytesInput[(byteIdx+1)],
                            bytesInput[(byteIdx+2)],
                            bytesInput[(byteIdx+3)]);

                        byteIdx = idx - w;

                        Rgba32 above = new(
                            bytesInput[(byteIdx+0)],
                            bytesInput[(byteIdx+1)],
                            bytesInput[(byteIdx+2)],
                            bytesInput[(byteIdx+3)]);

                        byteIdx = idx - 1;
                        Rgba32 left = new(
                            bytesInput[(byteIdx+0)],
                            bytesInput[(byteIdx+1)],
                            bytesInput[(byteIdx+2)],
                            bytesInput[(byteIdx+3)]);

                        byteIdx = idx + 1;
                        Rgba32 right = new(
                            bytesInput[(byteIdx+0)],
                            bytesInput[(byteIdx+1)],
                            bytesInput[(byteIdx+2)],
                            bytesInput[(byteIdx+3)]);

                        byteIdx = idx + w;
                        Rgba32 below = new(
                            bytesInput[(byteIdx+0)],
                            bytesInput[(byteIdx+1)],
                            bytesInput[(byteIdx+2)],
                            bytesInput[(byteIdx+3)]);

                        lightness =
                            this.Sample(current) * 4 - 
                            this.Sample(above) -
                            this.Sample(left) -
                            this.Sample(right) -
                            this.Sample(below);
                    }

                    pixelsOutput[x].G = (byte)Math.Min(byte.MaxValue, Math.Max(0, Math.Round(lightness)));

                    ++idx;
                }
            }
        }

        private void SkinDetect(Image<Rgba32> input, Image<Rgba32> output)
        {
            float SkinColor(Rgba32 pixel, (float red, float green, float blue) skinColor)
            {
                var blue = Blue(pixel);
                var green = Green(pixel);
                var red = Red(pixel);
                var mag = (float)Math.Sqrt(red * red + green * green + blue * blue);
                var rd = red / mag - skinColor.red;
                var gd = green / mag - skinColor.green;
                var bd = blue / mag - skinColor.blue;
                var d = (float)Math.Sqrt(rd * rd + gd * gd + bd * bd);
                return 1f - d;
            }

            for (var y = 0; y < input.Height; y++)
            {
                Span<Rgba32> pixelsInput = input.GetPixelRowSpan(y);
                Span<Rgba32> pixelsOutput = output.GetPixelRowSpan(y);

                for (var x = 0; x < input.Width; x++)
                {
                    var lightness = this.Cie(pixelsInput[x]) / 255f;
                    var skin = SkinColor(pixelsInput[x], this.options.SkinColor);
                    var isSkinColor = skin > this.Options.SkinThreshold;
                    var isSkinBrightness =
                        lightness >= this.Options.SkinBrightnessMin &&
                        lightness <= this.Options.SkinBrightnessMax;

                    if (isSkinColor && isSkinBrightness)
                    {
                        pixelsOutput[x].R = (byte)Math.Min(byte.MaxValue, (skin - this.Options.SkinThreshold) * (255f / (1f - this.Options.SkinThreshold)));
                    }
                    else
                    {
                        pixelsOutput[x].R = 0;
                    }
                }
            }
        }

        private void SaturationDetect(Image<Rgba32> input, Image<Rgba32> output)
        {
            float Saturation(Rgba32 pixel)
            {
                var blue = Blue(pixel);
                var green = Green(pixel);
                var red = Red(pixel);

                var maximum = Math.Max(red / 255f, Math.Max(green / 255f, blue / 255f));
                var minumum = Math.Min(red / 255f, Math.Min(green / 255f, blue / 255f));

                if (maximum == minumum)
                {
                    return 0f;
                }

                var l = (maximum + minumum) / 2;
                var d = maximum - minumum;

                return l > 0.5f ? d / (2 - maximum - minumum) : d / (maximum + minumum);
            }

            for (var y = 0; y < input.Height; y++)
            {
                Span<Rgba32> pixelsInput = input.GetPixelRowSpan(y);
                Span<Rgba32> pixelsOutput = output.GetPixelRowSpan(y);

                for (var x = 0; x < input.Width; x++)
                {
                    var lightness = this.Cie(pixelsInput[x]) / 255f;
                    var sat = Saturation(pixelsInput[x]);

                    var acceptableSaturation = sat > this.Options.SaturationThreshold;
                    var acceptableLightness =
                        lightness >= this.Options.SaturationBrightnessMin &&
                        lightness <= this.Options.SaturationBrightnessMax;

                    if (acceptableLightness && acceptableSaturation)
                    {
                        pixelsOutput[x].B = (byte)Math.Min(byte.MaxValue, (sat - this.Options.SaturationThreshold) * (255f / (1f - this.Options.SaturationThreshold)));
                    }
                    else
                    {
                        pixelsOutput[x].B = 0;
                    }
                }
            }
        }

        /// <summary>
        /// The DownSample method divides the input image to (factor x factor) sized areas and reduces each of them to one pixel in the output image.
        /// Because not every image can be divided by (factor), the last pixels on the right and the bottom might not be included in the calculation.
        /// </summary>
        private Image<Rgba32> DownSample(Image<Rgba32> image)
        {
            int factor = this.Options.ScoreDownSample;

            // Math.Floor instead of Math.Round to avoid a (factor + 1)th area on the right/bottom
            var width = (int)Math.Floor(image.Width / (float)factor);
            var height = (int)Math.Floor(image.Height / (float)factor);
            var output = new Image<Rgba32>(width, height);

            var ifactor2 = 1f / (factor * factor);

            for (var y = 0; y < height; y++)
            {
                Span<Rgba32> pixelsInput = image.GetPixelRowSpan(y);
                Span<Rgba32> pixelsOutput = output.GetPixelRowSpan(y);

                byte[] bytesInput;

                if (image.TryGetSinglePixelSpan(out var pixelSpan)) {
                    bytesInput = MemoryMarshal.AsBytes(pixelSpan).ToArray();
                } else {
                    throw new Exception("Issue with memory");
                }

                for (var x = 0; x < width; x++)
                {
                    var r = 0;
                    var g = 0;
                    var b = 0;
                    var a = 0;

                    var mr = 0;
                    var mg = 0;

                    for (var v = 0; v < factor; v++)
                    {
                        for (var u = 0; u < factor; u++)
                        {
                            var j = (y * factor + v) * image.Width + (x * factor + u);

                            var pixel = new Rgba32(bytesInput[4 * j]);

                            r += Red(pixel);
                            g += Green(pixel);
                            b += Blue(pixel);
                            a += Alpha(pixel);

                            mr = Math.Max(mr, Red(pixel));
                            mg = Math.Max(mg, Green(pixel));
                            // unused
                            // mb = Math.Max(mb, *Blue(pixel, info.ColorType));
                        }
                    }
                    // this is some funky magic to preserve detail a bit more for
                    // skin (r) and detail (g). Saturation (b) does not get this boost.


                    pixelsOutput[x] = new Rgba32(
                        (byte)(r * ifactor2 * 0.5f + mr * 0.5f),
                        (byte)(g * ifactor2 * 0.7f + mg * 0.3f),
                        (byte)(b * ifactor2),
                        (byte)(a * ifactor2));
                }
            }

            return output;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Sample(Rgba32 ptr)
        {
            return this.Cie(ptr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Cie(Rgba32 ptr)
        {
            return 0.5126f * Blue(ptr)
                 + 0.7152f * Green(ptr)
                 + 0.0722f * Red(ptr);
        }

        private void ApplyBoosts(Image<Rgba32> image, params BoostArea[] boostAreas)
        {
            byte[] bytesInput;

            if (image.TryGetSinglePixelSpan(out var pixelSpan)) {
                bytesInput = MemoryMarshal.AsBytes(pixelSpan).ToArray();
            } else {
                throw new Exception("Issue with memory");
            }

            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    bytesInput[((y * image.Width + x))] = 0;
                }
            }

            foreach (var boostArea in boostAreas)
            {
                var x0 = boostArea.Area.X;
                var x1 = boostArea.Area.X + boostArea.Area.Width;
                var y0 = boostArea.Area.Y;
                var y1 = boostArea.Area.Y + boostArea.Area.Height;
                var weight = boostArea.Weight * 255;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var alpha = new Rgba32(bytesInput[(y * image.Width + x) * 4]).A;

                        bytesInput[(y * image.Width + x) * 4] = (byte)Math.Max(0, Math.Min(byte.MaxValue, alpha + weight));
                    }
                }
            }
        }

        private IReadOnlyList<Crop> GenerateCrops(int width, int height)
        {
            var results = new List<Crop>();
            var minDimension = Math.Min(width, height);
            var cropWidth = this.Options.CropWidth ?? minDimension;
            var cropHeight = this.Options.CropHeight ?? minDimension;

            for (var scale = this.Options.MaxScale; scale >= this.Options.MinScale; scale -= this.Options.ScaleStep)
            {
                for (var y = 0; y + cropHeight * scale <= height; y += this.Options.Step)
                {
                    for (var x = 0; x + cropWidth * scale <= width; x += this.Options.Step)
                    {
                        results.Add(new Crop(new Rectangle(x, y, (int)Math.Round(cropWidth * scale), (int)Math.Round(cropHeight * scale))));
                    }
                }
            }

            return results;
        }

        private Score Score(Image<Rgba32> output, Rectangle crop, BoostArea[] boostAreas)
        {
            var result = new Score();

            var downSample = this.Options.ScoreDownSample;
            var invDownSample = 1 / (double)downSample;
            var outputHeightDownSample = output.Height * downSample;
            var outputWidthDownSample = output.Width * downSample;
            var outputWidth = output.Width;

            byte[] bytesInput;

            if (output.TryGetSinglePixelSpan(out var pixelSpan)) {
                bytesInput = MemoryMarshal.AsBytes(pixelSpan).ToArray();
            } else {
                throw new Exception("Issue with memory");
            }

            for (var y = 0; y < outputHeightDownSample; y += downSample)
            {
                for (var x = 0; x < outputWidthDownSample; x += downSample)
                {
                    var pixel = new Rgba32(bytesInput[(((int)(y * invDownSample)) * outputWidth + ((int)(x * invDownSample))) * 4]);

                    var i = this.Importance(crop, x, y);
                    var detail = Green(pixel) / 255f;

                    result.Detail += detail * i;
                    result.Skin += Red(pixel) / 255f * (detail + this.Options.SkinBias) * i;
                    result.Saturation += Blue(pixel) / 255f * (detail + this.Options.SaturationBias) * i;
                    result.Boost += Alpha(pixel) / 255f * i;
                }
            }

            if (boostAreas.Any())
            {
                foreach (var boostArea in boostAreas)
                {
                    if (crop.Contains(boostArea.Area))
                    {
                        continue;
                    }

                    if (boostArea.Area.IntersectsWith(crop))
                    {
                        result.Penalty += boostArea.Weight;
                    }
                }

                result.Penalty /= boostAreas.Length;
            }

            result.Total =
              (result.Detail * this.Options.DetailWeight +
               result.Skin * this.Options.SkinWeight +
               result.Saturation * this.Options.SaturationWeight +
               result.Boost * this.Options.BoostWeight) / (crop.Width * crop.Height);

            result.Total -= result.Total * result.Penalty;

            return result;
        }

        private float Importance(Rectangle crop, float x, float y)
        {
            if (crop.X > x || x >= crop.X + crop.Width || crop.Y > y || y >= crop.Y + crop.Height)
            {
                return this.Options.OutsideImportance;
            }

            x = (x - crop.X) / crop.Width;
            y = (y - crop.Y) / crop.Height;
            var px = Math.Abs(0.5f - x) * 2;
            var py = Math.Abs(0.5f - y) * 2;

            // Distance from edge
            var dx = Math.Max(px - 1.0f + this.Options.EdgeRadius, 0);
            var dy = Math.Max(py - 1.0f + this.Options.EdgeRadius, 0);
            var d = (dx * dx + dy * dy) * this.Options.edgeWeight;
            var s = 1.41f - (float)Math.Sqrt(px * px + py * py);
            if (this.Options.RuleOfThirds)
            {
                s += Math.Max(0, s + d + 0.5f) * 1.2f * (Thirds(px) + Thirds(py));
            }
            return s + d;
        }

        // Gets value in the range of [0; 1] where 0 is the center of the pictures
        // returns weight of rule of thirds [0; 1]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Thirds(float x)
        {
            x = (((x - 1f / 3f + 1f) % 2f) * 0.5f - 0.5f) * 16;
            return Math.Max(1f - x * x, 0f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte Red(Rgba32 ptr) => ptr.R;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte Green(Rgba32 ptr) => ptr.G;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte Blue(Rgba32 ptr) => ptr.B;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte Alpha(Rgba32 ptr) => ptr.A;
    }
}
