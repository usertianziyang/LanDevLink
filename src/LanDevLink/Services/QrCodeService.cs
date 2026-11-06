using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace LanDevLink.Services;

public static class QrCodeService
{
    public static BitmapImage Create(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        var bytes = code.GetGraphic(8, [29, 29, 31, 255], [255, 255, 255, 255], true);

        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

