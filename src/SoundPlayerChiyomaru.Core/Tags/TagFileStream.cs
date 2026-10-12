namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>
/// タグを読むためのファイルのストリーム。長さを開いた時点で 1 回だけ取る。
/// 他のソフトの書き込みを許して開いたファイルでは、長さを尋ねるたびに OS への問い合わせになり、
/// 残りの長さを確かめながら小さな単位で読み進める処理が極端に遅くなるため。
/// </summary>
internal sealed class TagFileStream(FileStream inner) : Stream
{
    private readonly long length = inner.Length;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override int ReadByte() => inner.ReadByte();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
