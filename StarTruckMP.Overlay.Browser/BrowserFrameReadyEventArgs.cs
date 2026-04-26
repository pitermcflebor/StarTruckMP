namespace StarTruckMP.Overlay.Browser;

public sealed class BrowserFrameReadyEventArgs : EventArgs
{
    public BrowserFrameReadyEventArgs(byte[] buffer, int width, int height, int stride)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public byte[] Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
}

