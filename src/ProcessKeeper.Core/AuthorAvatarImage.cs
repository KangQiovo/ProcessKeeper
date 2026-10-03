namespace ProcessKeeper.Core;

internal static class AuthorAvatarImage
{
    internal static bool IsSupported(byte[] bytes)
        => bytes.Length >= 8 && bytes.Length <= 256 * 1024 && (IsPng(bytes) || IsJpeg(bytes));

    private static bool IsPng(byte[] bytes)
    {
        if (bytes.Length < 45 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71
            || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10) return false;
        int offset = 8, chunks = 0, color = -1;
        bool header = false, data = false, palette = false, endedData = false;
        while (offset <= bytes.Length - 12 && ++chunks <= 4096)
        {
            uint size = U32(bytes, offset), kind = U32(bytes, offset + 4);
            if (size > bytes.Length - offset - 12) return false;
            int length = (int)size;
            if (Crc(bytes, offset + 4, length + 4) != U32(bytes, offset + 8 + length)) return false;
            if (!header && kind != 0x49484452) return false;
            if (data && kind != 0x49444154) endedData = true;
            if (kind == 0x49484452) // IHDR
            {
                if (header || length != 13) return false;
                uint width = U32(bytes, offset + 8), height = U32(bytes, offset + 12);
                if (width < 1 || height < 1 || width > 1024 || height > 1024) return false;
                int depth = bytes[offset + 16]; color = bytes[offset + 17];
                bool supported = color switch
                {
                    0 => depth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => depth is 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8,
                    _ => false
                };
                if (!supported || bytes[offset + 18] != 0 || bytes[offset + 19] != 0 || bytes[offset + 20] > 1) return false;
                header = true;
            }
            else if (kind == 0x504c5445) // PLTE
            {
                if (palette || data || length < 3 || length > 768 || length % 3 != 0 || color is 0 or 4) return false;
                palette = true;
            }
            else if (kind == 0x49444154) // IDAT
            {
                if (endedData || color == 3 && !palette) return false;
                data = true;
            }
            else if (kind == 0x49454e44) // IEND
                return header && data && length == 0 && offset + 12 == bytes.Length;
            else if (kind is 0x6163544c or 0x6663544c or 0x66644154 || (bytes[offset + 4] & 32) == 0)
                return false; // No animated PNG or unrecognized critical chunks.
            offset += length + 12;
        }
        return false;
    }

    private static bool IsJpeg(byte[] bytes)
    {
        if (bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        int offset = 2, components = 0;
        bool frame = false, scan = false;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 0xff) return false;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) return false;
            int marker = bytes[offset++];
            if (marker == 0xd9) return frame && scan && offset == bytes.Length;
            if (marker is 0x00 or 0xd8 || marker >= 0xd0 && marker <= 0xd7) return false;
            if (offset + 2 > bytes.Length) return false;
            int length = U16(bytes, offset);
            if (length < 2 || length > bytes.Length - offset) return false;
            if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc)
            {
                // Baseline and progressive 8-bit JPEG only; no lossless/differential modes.
                if (frame || marker is not (0xc0 or 0xc2) || length < 8 || bytes[offset + 2] != 8) return false;
                int height = U16(bytes, offset + 3), width = U16(bytes, offset + 5);
                components = bytes[offset + 7];
                if (width < 1 || height < 1 || width > 1024 || height > 1024
                    || components is not (1 or 3 or 4) || length != 8 + components * 3) return false;
                frame = true;
            }
            if (marker == 0xdc) return false; // DNL must not replace a previously checked height.
            if (marker == 0xda)
            {
                if (!frame || length < 6 || bytes[offset + 2] < 1 || bytes[offset + 2] > components
                    || length != 6 + bytes[offset + 2] * 2) return false;
                scan = true;
                offset += length;
                // Entropy data escapes literal FF and may contain restart markers. The next real
                // marker returns to the segment parser (also handles multiple progressive scans).
                while (offset < bytes.Length)
                {
                    if (bytes[offset] != 0xff) { offset++; continue; }
                    if (offset + 1 >= bytes.Length) return false;
                    int next = bytes[offset + 1];
                    if (next == 0 || next >= 0xd0 && next <= 0xd7) { offset += 2; continue; }
                    break;
                }
            }
            else offset += length;
        }
        return false;
    }

    private static int U16(byte[] bytes, int offset) => bytes[offset] * 256 + bytes[offset + 1];
    private static uint U32(byte[] bytes, int offset)
        => (uint)bytes[offset] << 24 | (uint)bytes[offset + 1] << 16 | (uint)bytes[offset + 2] << 8 | bytes[offset + 3];
    private static uint Crc(byte[] bytes, int offset, int count)
    {
        uint crc = uint.MaxValue;
        for (int i = offset; i < offset + count; i++)
        {
            crc ^= bytes[i];
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0U);
        }
        return ~crc;
    }
}
