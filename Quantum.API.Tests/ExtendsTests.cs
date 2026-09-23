using Quantum.Entities.Config;
using Quantum.Entities.Model;
using Quantum.Utils;
using System.Text;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// Extends 扩展方法中的纯函数与可离线验证的逻辑。
/// </summary>
public class ExtendsTests
{
    [Fact]
    public void RemoveEmoji_WithEmojiStripsEmoji()
    {
        // 😀 为 UTF-32 代理对（UTF32 编码首两字节 253/255），应被移除
        var result = "你好😀世界".RemoveEmoji();
        Assert.Equal("你好世界", result);
    }

    [Fact]
    public void RemoveEmoji_WithoutEmojiKeepsText()
    {
        Assert.Equal("普通文本123", "普通文本123".RemoveEmoji());
    }

    [Fact]
    public void RemoveEmoji_WithCustomReplacement_SubstitutesOncePerEmoji()
    {
        // 2026-09-19 rune 重写修复：旧实现按 char 遍历，代理对高/低两半各 Replace 一次，
        // 非空 replace 会被插入两次（曾作为已知缺陷锁定用例）；现一整个 emoji rune 替换一次
        Assert.Equal("你好[表情]世界", "你好😀世界".RemoveEmoji("[表情]"));
    }

    [Fact]
    public void RemoveEmoji_EmptyStringReturnsEmpty()
    {
        Assert.Equal("", "".RemoveEmoji());
    }

    [Fact]
    public void DeepClone_ProducesEqualButDistinctInstance()
    {
        var task = new TaskModel
        {
            Name = "clone-me",
            Command = "echo hi",
            Cron = "0 * * * * ?",
            Enable = true,
            DayLimit = 7
        };
        var clone = task.DeepClone();
        Assert.NotSame(task, clone);
        Assert.Equal(task.Name, clone.Name);
        Assert.Equal(task.Command, clone.Command);
        Assert.Equal(task.Cron, clone.Cron);
        Assert.Equal(task.Enable, clone.Enable);
        Assert.Equal(task.DayLimit, clone.DayLimit);
    }

    [Fact]
    public void ToUnix_MatchesKnownEpoch()
    {
        // ToUnix 以 TimeZone.CurrentTimeZone.ToLocalTime(1970-1-1) 为基准（本机时区），
        // 因此同一时区内 (t - 本地纪元).TotalSeconds 与之自洽。
        var localEpoch = TimeZone.CurrentTimeZone.ToLocalTime(new DateTime(1970, 1, 1));
        var known = new DateTime(2026, 1, 2, 3, 4, 5);
        Assert.Equal((long)(known - localEpoch).TotalSeconds, known.ToUnix());
    }

    [Fact]
    public void ConvertDateTimeTolong_Returns13DigitMilliseconds()
    {
        var dt = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Local);
        var ms = dt.ConvertDateTimeTolong();
        Assert.InRange(ms, 1_000_000_000_000, 9_999_999_999_999);
        // 与毫秒差公式自洽
        var expected = (dt.Ticks - new DateTime(1970, 1, 1).Ticks) / 10000;
        Assert.Equal(expected, ms);
    }

    [Fact]
    public void Aes_RoundTripFormatIsStable()
    {
        // 登录/改密均用 Aes(password, key, 9) 存密码，这里锁定格式与可解性
        var key = new string('K', 48);
        var cipher = Extends.Aes("secret-password", key, 9);
        Assert.StartsWith("A9/", cipher);

        // 用同样方式解密验证内容一致
        var raw = cipher[3..];
        var keyBytes = Encoding.UTF8.GetBytes(key.Substring(9, 32));
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = keyBytes;
        aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        var plain = aes.CreateDecryptor().TransformFinalBlock(
            Convert.FromBase64String(raw), 0, Convert.FromBase64String(raw).Length);
        Assert.Equal("secret-password", Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public void Version_FollowsVersioningScheme()
    {
        // 格式 V{年-2021}.{MM}.{dd} Beta {秒}
        Assert.Matches(@"^V\d+\.\d{2}\.\d{2} Beta \d+$", Extends.Version);
    }

    [Fact]
    public void CopyFolder_CopiesAllowedExtensionsOnly()
    {
        var src = Path.Combine(Path.GetTempPath(), "qtest-copy-src-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(Path.GetTempPath(), "qtest-copy-dest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "a.txt"), "text");
            File.WriteAllText(Path.Combine(src, "b.exe"), "binary");
            File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");

            Extends.CopyFolder(src, dest, [".txt"]);

            Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "b.exe")));
            Assert.True(File.Exists(Path.Combine(dest, "sub", "c.txt")));
        }
        finally
        {
            Directory.Delete(src, true);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
        }
    }

    [Fact]
    public void CopyFolder_SkipsGitDirectories()
    {
        // 语义：源路径（含每次递归传入的子目录路径）含 ".git" 即整体短路，
        // 因此任意层级下的 .git 文件夹都不会被复制。
        var src = Path.Combine(Path.GetTempPath(), "qtest-git-src-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(Path.GetTempPath(), "qtest-git-dest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var normal = Path.Combine(src, "scripts");
            Directory.CreateDirectory(normal);
            File.WriteAllText(Path.Combine(normal, "a.txt"), "text");
            var gitDir = Path.Combine(src, ".git", "hooks");
            Directory.CreateDirectory(gitDir);
            File.WriteAllText(Path.Combine(gitDir, "pre-commit"), "#!/bin/sh");

            Extends.CopyFolder(src, dest, [".txt", ".sh", ""]);

            Assert.True(File.Exists(Path.Combine(dest, "scripts", "a.txt")));
            Assert.False(Directory.Exists(Path.Combine(dest, ".git")), ".git 目录应被整体跳过");
        }
        finally
        {
            Directory.Delete(src, true);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
        }
    }

    [Fact]
    public void CopyFolder_SourcePathContainingGit_IsSkippedEntirely()
    {
        // 源路径本身含 ".git" 时整体短路（防脚本目录快照回写 git 仓库）
        var src = Path.Combine(Path.GetTempPath(), "qtest-git-src-" + Guid.NewGuid().ToString("N"), ".git");
        var dest = Path.Combine(Path.GetTempPath(), "qtest-git-dest-" + Guid.NewGuid().ToString("N"));
        var parent = Path.GetDirectoryName(src)!;
        try
        {
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "config"), "[core]");

            Extends.CopyFolder(src, dest, [".txt"]);

            Assert.False(Directory.Exists(dest));
        }
        finally
        {
            Directory.Delete(parent, true);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
        }
    }

    [Fact]
    public void RemoveEmoji_RemovesReplacementCharAndKeepsBmpSymbols()
    {
        // U+FFFD（含无效代理序列，EnumerateRunes 亦产出 U+FFFD）一并剔除；
        // BMP 符号（< U+1F000）不属剔除范围，与旧实现实际行为一致
        Assert.Equal("ab", "a\uFFFDb".RemoveEmoji());
        Assert.Equal("☀音乐", "☀音乐".RemoveEmoji());
    }

    [Fact]
    public void RemoveEmoji_KeepsCjkExtBRareChar_StripsEmojiPlane()
    {
        // §2-13：U+20000–U+2FFFF 表意补充平面（生僻字）保留；U+1F000–U+1FFFF 的 emoji 仍剔除
        Assert.Equal("𠀀", "𠀀".RemoveEmoji());        // U+20000 CJK 扩展 B 保留
        Assert.Equal("𠀀A", "𠀀A".RemoveEmoji());
        Assert.Equal("A", "A🀄".RemoveEmoji());          // U+1F004 麻将 emoji 剔除
    }
}
