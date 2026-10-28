// SPDX-License-Identifier: GPL-3.0-or-later
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;

namespace Keepassity.Mock;

/// <summary>Decodes an otpauth QR image or a clipboard screenshot in the UI layer.</summary>
public static class QrDecoder
{
    public static async Task<string?> DecodeFileAsync(StorageFile file)
    {
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        return await DecodeStreamAsync(stream);
    }

    public static async Task<string?> DecodeClipboardAsync()
    {
        DataPackageView content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.Bitmap))
        {
            RandomAccessStreamReference reference = await content.GetBitmapAsync();
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            return await DecodeStreamAsync(stream);
        }
        if (content.Contains(StandardDataFormats.Text))
        {
            string text = (await content.GetTextAsync()).Trim();
            return text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)
                ? text
                : null;
        }
        return null;
    }

    private static async Task<string?> DecodeStreamAsync(IRandomAccessStream stream)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] data = pixels.DetachPixelData();
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
            },
        };
        Result? result = reader.Decode(data, (int)decoder.PixelWidth,
            (int)decoder.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32);
        return result?.Text?.Trim();
    }
}
