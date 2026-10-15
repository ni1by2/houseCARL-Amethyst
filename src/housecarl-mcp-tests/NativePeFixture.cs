namespace HousecarlMcpTests;

/// <summary>A small authored AMD64 PE DLL with a known import descriptor, independent of host system files.</summary>
static class NativePeFixture
{
    public static byte[] Bytes()
    {
        var image = new byte[1024];
        void U16(int at, ushort value) => BitConverter.GetBytes(value).CopyTo(image, at);
        void U32(int at, uint value) => BitConverter.GetBytes(value).CopyTo(image, at);
        U16(0, 0x5A4D); U32(0x3C, 0x80); U32(0x80, 0x4550);
        U16(0x84, 0x8664); U16(0x86, 1); U16(0x94, 240); U16(0x96, 0x2022);
        const int optional = 0x98;
        U16(optional, 0x20B); U32(optional + 0x20, 0x1000); U32(optional + 0x24, 0x200);
        U32(optional + 0x38, 0x2000); U32(optional + 0x3C, 0x200); U16(optional + 0x44, 3);
        U32(optional + 0x6C, 16); U32(optional + 0x78, 0x1000); U32(optional + 0x7C, 40);
        const int section = optional + 240;
        System.Text.Encoding.ASCII.GetBytes(".idata").CopyTo(image, section);
        U32(section + 8, 0x200); U32(section + 12, 0x1000); U32(section + 16, 0x200);
        U32(section + 20, 0x200); U32(section + 36, 0x40000040);
        U32(0x20C, 0x1040);
        System.Text.Encoding.ASCII.GetBytes("KERNEL32.dll\0").CopyTo(image, 0x240);
        return image;
    }
}
