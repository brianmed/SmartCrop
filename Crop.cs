using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BrianMed.SmartCrop
{
    public class Crop
    {
        public Crop(Rectangle area)
        {
            this.Area = area;
        }

        public Rectangle Area { get; internal set; }
        public Score Score { get; internal set; }
    }
}
