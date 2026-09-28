using System.Text;

namespace Quantum.Application.Channels;

/// <summary>
/// 飞书长连接私有帧协议（protobuf 手工 wire-format 编解码）。
///
/// 为什么自己实现而不是用社区包：帧解析是本通道最易出现**静默故障**的一环——解错了不会抛异常，
/// 只是收不到事件，排查成本极高。自实现可对编解码写完整往返测试（FrameCodec_RoundTrips*），
/// 且不引入外部依赖。字段布局与官方 SDK（larksuite/oapi-sdk-go 的 pbbp2）一致：
///   1 SeqID(varint) 2 LogID(varint) 3 service(varint) 4 method(varint)
///   5 headers(Header{key,value} 重复) 6 payload_encoding(string) 7 payload_type(string)
///   8 payload(bytes) 9 LogIDNew(string)
/// method：0=控制帧(Ping/Pong) 1=数据帧(事件/回调)。
/// </summary>
public sealed class FeishuFrame
{
    public const int MethodControl = 0;
    public const int MethodData = 1;

    public long SeqId { get; set; }

    /// <summary>
    /// 默认必须是 0（控制帧）而非 1：protobuf 省略零值，method=0 的控制帧在报文里**没有该字段**，
    /// 若解码默认值取 1，所有 Ping 都会被误判成数据帧。
    /// </summary>
    public int Method { get; set; } = MethodControl;
    public int Service { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.Ordinal);
    public string PayloadEncoding { get; set; } = "utf-8";
    public string PayloadType { get; set; }
    public byte[] Payload { get; set; } = [];

    public string PayloadText => Payload.Length == 0 ? "" : Encoding.UTF8.GetString(Payload);

    public string Header(string key) => Headers.TryGetValue(key, out var value) ? value : null;
}

public static class FeishuFrameCodec
{
    private const int FieldSeqId = 1;
    private const int FieldLogId = 2;
    private const int FieldService = 3;
    private const int FieldMethod = 4;
    private const int FieldHeaders = 5;
    private const int FieldPayloadEncoding = 6;
    private const int FieldPayloadType = 7;
    private const int FieldPayload = 8;
    private const int FieldLogIdNew = 9;

    private const int HeaderKey = 1;
    private const int HeaderValue = 2;

    // 帧内单个字段与 header 子消息的上限：用于在分配前挡住畸形/恶意长度，
    // 避免一个伪造的超大长度直接吃掉进程内存。
    private const int MaxFieldBytes = 8 * 1024 * 1024;

    public static byte[] Encode(FeishuFrame frame)
    {
        using var buffer = new MemoryStream();
        if (frame.SeqId != 0) WriteVarintField(buffer, FieldSeqId, (ulong)frame.SeqId);
        if (frame.Service != 0) WriteVarintField(buffer, FieldService, (ulong)frame.Service);
        if (frame.Method != 0) WriteVarintField(buffer, FieldMethod, (ulong)frame.Method);

        foreach (var pair in frame.Headers)
        {
            using var sub = new MemoryStream();
            WriteString(sub, HeaderKey, pair.Key);
            WriteString(sub, HeaderValue, pair.Value ?? "");
            WriteBytes(buffer, FieldHeaders, sub.ToArray());
        }

        WriteString(buffer, FieldPayloadEncoding, frame.PayloadEncoding);
        if (!string.IsNullOrEmpty(frame.PayloadType)) WriteString(buffer, FieldPayloadType, frame.PayloadType);
        if (frame.Payload.Length > 0) WriteBytes(buffer, FieldPayload, frame.Payload);
        return buffer.ToArray();
    }

    public static FeishuFrame Decode(byte[] data)
    {
        if (data is null || data.Length == 0) throw new ChannelProtocolException("FEISHU_FRAME_EMPTY");
        var frame = new FeishuFrame();
        var offset = 0;
        while (offset < data.Length)
        {
            var tag = ReadVarint(data, ref offset);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 0x7);
            switch (field)
            {
                case FieldSeqId: frame.SeqId = (long)ReadVarintField(data, ref offset, wire); break;
                case FieldService: frame.Service = (int)ReadVarintField(data, ref offset, wire); break;
                case FieldMethod: frame.Method = (int)ReadVarintField(data, ref offset, wire); break;
                case FieldLogId: _ = ReadVarintField(data, ref offset, wire); break;
                case FieldHeaders:
                    var sub = ReadBytesField(data, ref offset, wire);
                    ReadHeaders(sub, frame.Headers);
                    break;
                case FieldPayloadEncoding: frame.PayloadEncoding = ReadStringField(data, ref offset, wire); break;
                case FieldPayloadType: frame.PayloadType = ReadStringField(data, ref offset, wire); break;
                case FieldPayload: frame.Payload = ReadBytesField(data, ref offset, wire); break;
                case FieldLogIdNew: _ = ReadStringField(data, ref offset, wire); break;
                default: SkipField(data, ref offset, wire); break;
            }
        }
        return frame;
    }

    private static void ReadHeaders(byte[] sub, Dictionary<string, string> into)
    {
        var offset = 0;
        string key = null, value = null;
        while (offset < sub.Length)
        {
            var tag = ReadVarint(sub, ref offset);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 0x7);
            if (field == HeaderKey) key = ReadStringField(sub, ref offset, wire);
            else if (field == HeaderValue) value = ReadStringField(sub, ref offset, wire);
            else SkipField(sub, ref offset, wire);
        }
        if (!string.IsNullOrEmpty(key)) into[key] = value ?? "";
    }

    // ── 写原语 ────────────────────────────────────────────────
    private static void WriteVarintField(Stream stream, int field, ulong value)
    {
        WriteVarint(stream, ((ulong)field << 3) | 0);
        WriteVarint(stream, value);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        Span<byte> tmp = stackalloc byte[10];
        var written = 0;
        while (value >= 0x80)
        {
            tmp[written++] = (byte)(value | 0x80);
            value >>= 7;
        }
        tmp[written++] = (byte)value;
        stream.Write(tmp[..written]);
    }

    private static void WriteBytes(Stream stream, int field, byte[] value)
    {
        WriteVarint(stream, ((ulong)field << 3) | 2);
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    private static void WriteString(Stream stream, int field, string value)
        => WriteBytes(stream, field, Encoding.UTF8.GetBytes(value ?? ""));

    // ── 读原语 ────────────────────────────────────────────────
    private static ulong ReadVarint(byte[] data, ref int offset)
    {
        ulong result = 0;
        var shift = 0;
        while (offset < data.Length)
        {
            if (shift > 63) throw new ChannelProtocolException("FEISHU_VARINT_OVERFLOW");
            var b = data[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        throw new ChannelProtocolException("FEISHU_VARINT_TRUNCATED");
    }

    private static ulong ReadVarintField(byte[] data, ref int offset, int wire)
    {
        if (wire != 0) { SkipField(data, ref offset, wire); return 0; }
        return ReadVarint(data, ref offset);
    }

    private static string ReadStringField(byte[] data, ref int offset, int wire)
        => wire == 2 ? Encoding.UTF8.GetString(ReadBytesField(data, ref offset, wire)) : "";

    private static byte[] ReadBytesField(byte[] data, ref int offset, int wire)
    {
        if (wire != 2) { SkipField(data, ref offset, wire); return []; }
        var length = (int)ReadVarint(data, ref offset);
        if (length < 0 || length > MaxFieldBytes || offset + length > data.Length)
            throw new ChannelProtocolException("FEISHU_FRAME_LENGTH_INVALID");
        var slice = new byte[length];
        Array.Copy(data, offset, slice, 0, length);
        offset += length;
        return slice;
    }

    private static void SkipField(byte[] data, ref int offset, int wire)
    {
        switch (wire)
        {
            case 0: _ = ReadVarint(data, ref offset); break;
            case 1: offset += 8; break;
            case 2:
            {
                var length = (int)ReadVarint(data, ref offset);
                if (length < 0 || offset + length > data.Length) throw new ChannelProtocolException("FEISHU_FRAME_LENGTH_INVALID");
                offset += length;
                break;
            }
            case 5: offset += 4; break;
            default: throw new ChannelProtocolException("FEISHU_WIRE_TYPE_UNSUPPORTED");
        }
        if (offset > data.Length) throw new ChannelProtocolException("FEISHU_FRAME_TRUNCATED");
    }
}
