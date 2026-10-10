using Quantum.Application.Channels;

namespace Quantum.API.Tests;

/// <summary>
/// 飞书帧编解码「长度前缀」整数溢出回归（对抗性审计 FIX-1）。
///
/// wire type 2 的声明长度与当前 offset 相加在 int 上回绕（如 length = int.MaxValue）会变负，
/// 使 `offset + length > data.Length` 截断检查放行：offset 随之为负，外层 while 循环下一次
/// ReadVarint 以负下标取址 → IndexOutOfRangeException。该异常**不是** ChannelProtocolException，
/// 会穿透 FeishuWssWorker.ReceiveLoopAsync 的 `catch (ChannelProtocolException)` 直达连接层兜底。
/// 本组用例钉死：任何非法长度一律抛 ChannelProtocolException（绝不 IndexOutOfRange），
/// 且合法边界（长度恰好等于剩余字节数）与正常跳字段行为不受修复影响。
/// </summary>
public sealed class FeishuFrameCodecOverflowTests
{
    /// <summary>Decode 必须抛 ChannelProtocolException；抛其它类型（尤其 IndexOutOfRangeException）即判回归。</summary>
    private static void AssertProtocolRejection(byte[] data)
    {
        var error = Record.Exception(() => FeishuFrameCodec.Decode(data));
        // IsType 断言同时覆盖「未抛异常（null）」与「抛了别种异常（实际类型会打印在失败信息里）」
        Assert.IsType<ChannelProtocolException>(error);
    }

    [Fact]
    public void Decode_UnknownFieldHugeLength_ThrowsProtocolExceptionNotIndexOutOfRange()
    {
        // field 99 / wire 2（未知名 → SkipField）：tag 0x9A 0x06 = 794，长度 varint 0xFF FF FF FF 07 = int.MaxValue。
        // offset(7) + int.MaxValue 在 int 上回绕为负 → 旧校验放行 → offset 变负 → 负下标读越界
        AssertProtocolRejection([0x9A, 0x06, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x00]);
    }

    [Fact]
    public void Decode_KnownFieldUnexpectedWireTypeHugeLength_ThrowsProtocolException()
    {
        // field 1（SeqId）声明 wire 2：ReadVarintField 见 wire != 0 转给 SkipField，走同一条长度前缀路径
        AssertProtocolRejection([0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x00]);
    }

    [Fact]
    public void Decode_HeaderFieldHugeLength_RejectedByFieldCap()
    {
        // field 5（headers）走 ReadBytesField：int.MaxValue 必须先被 8MB 单字段上限挡住——
        // 该路径本就安全（长度先受 MaxFieldBytes 约束，不会回绕），此用例是防止将来改动把它一起带坏
        AssertProtocolRejection([0x2A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x00]);
    }

    [Fact]
    public void Decode_HugeButNonOverflowingLength_IsRejected()
    {
        // 100000000（varint 0x80 0xC2 0xD7 0x2F）：大但不触发 int 回绕，仍须按「超出剩余字节数」拒绝
        AssertProtocolRejection([0x9A, 0x06, 0x80, 0xC2, 0xD7, 0x2F, 0x00]);
    }

    [Fact]
    public void Decode_UnknownFieldLengthExactlyFitsRemainder_StillDecodes()
    {
        // 未知字段（field 99, wire 2）声明长度 2 恰好等于剩余字节数：合法边界必须继续解码，不得被一并拒掉
        var frame = FeishuFrameCodec.Decode([0x08, 0x07, 0x9A, 0x06, 0x02, 0xAA, 0xBB]);

        Assert.Equal(7, frame.SeqId);
    }

    [Fact]
    public void Decode_UnknownFieldSkippedThenNextField_StillDecodes()
    {
        // 未知字段（长度 2）+ 其后 field 1 varint 5：证明跳字段后游标仍正确推进
        var frame = FeishuFrameCodec.Decode([0x9A, 0x06, 0x02, 0xAA, 0xBB, 0x08, 0x05]);

        Assert.Equal(5, frame.SeqId);
    }

    /// <summary>
    /// 随机缓冲模糊测试（20 万条）：畸形帧只能以 ChannelProtocolException 被拒，**绝不允许**
    /// 漏出其它异常类型（IndexOutOfRange / ArgumentOutOfRange / Overflow 等）。后者一旦出现，
    /// 会穿透 FeishuWssWorker 只捕获 ChannelProtocolException 的循环，表现为连接层兜底。
    /// 随机源固定种子，失败可复现。
    /// </summary>
    [Fact]
    public void Decode_RandomBuffers_OnlyEverRejectWithProtocolException()
    {
        var random = new Random(20261011);
        var accepted = 0;
        for (var i = 0; i < 200_000; i++)
        {
            // 长度偏向 0-40 字节：足以覆盖 tag/varint/长度前缀各条路径；wire type 2 用较高概率，
            // 因为长度前缀分支正是本次修复的目标
            var length = random.Next(0, 41);
            var buffer = new byte[length];
            for (var b = 0; b < length; b++)
            {
                buffer[b] = random.Next(0, 100) < 35
                    ? (byte)((random.Next(1, 10) << 3) | 2) // 常见字段号 + wire 2
                    : (byte)random.Next(0, 256);
            }

            var error = Record.Exception(() => FeishuFrameCodec.Decode(buffer));
            if (error == null)
            {
                accepted++;
                continue;
            }
            Assert.IsType<ChannelProtocolException>(error);
        }

        // 随机数据里必然有能解出合法帧的（空帧/纯 varint 等），确认这组用例不是「全在测拒绝分支」
        Assert.True(accepted > 0, "20 万条随机数据中应至少有一条可解码，否则随机分布失真");
    }
}
