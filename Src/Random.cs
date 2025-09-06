namespace DiskTrip;

sealed class RandomXorshift
{
    private ulong _x = 123456789;
    private ulong _y = 362436069;
    private ulong _z = 521288629;
    private ulong _w = 88675123;

    public unsafe void NextBytes(byte[] buf)
    {
        if (buf.Length % 32 != 0)
            throw new ArgumentException("The buffer length must be a multiple of 32.", nameof(buf));
        ulong x = _x, y = _y, z = _z, w = _w;
        fixed (byte* pbytes = buf)
        {
            ulong* pbuf = (ulong*) pbytes;
            ulong* pend = (ulong*) (pbytes + buf.Length);
            while (pbuf < pend)
            {
                ulong tx = x ^ (x << 11);
                ulong ty = y ^ (y << 11);
                ulong tz = z ^ (z << 11);
                ulong tw = w ^ (w << 11);
                *(pbuf++) = x = w ^ (w >> 19) ^ (tx ^ (tx >> 8));
                *(pbuf++) = y = x ^ (x >> 19) ^ (ty ^ (ty >> 8));
                *(pbuf++) = z = y ^ (y >> 19) ^ (tz ^ (tz >> 8));
                *(pbuf++) = w = z ^ (z >> 19) ^ (tw ^ (tw >> 8));
            }
        }
        _x = x; _y = y; _z = z; _w = w;
    }
}
