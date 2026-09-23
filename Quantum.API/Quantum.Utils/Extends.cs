using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;

namespace Quantum.Utils;

public static class Extends
{
    public static DateTime VersionTime = new(2026, 9, 17, 0, 0, 1);

    // TemporaryToken（任务临时令牌签发）已随 2026-09-16 脚本执行引擎改造删除：
    // C# 任务门面直调应用服务（决议 6），任务执行链路不再签发/注入任何令牌

    public static string Version
    {
        get
        {
            return $"V{VersionTime.Year - 2021}.{VersionTime:MM}.{VersionTime:dd} Beta {VersionTime.Second}";
        }
    }

    /// <summary>
    ///     时间转时间戳
    /// </summary>
    public static long ConvertDateTimeTolong(this DateTime dateTime)
    {
        var startTime = new DateTime(1970, 1, 1, 0, 0, 0, 0);
        var t = (dateTime.Ticks - startTime.Ticks) / 10000; //除10000调整为13位      
        return t;
    }

    public static T DeepClone<T>(this T t)
    {
        return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(t));
    }

    /// <summary>
    /// 剔除字符串中的 emoji 与 U+FFFD 替换符（消息落库/日志热路径每条必经）。
    /// 旧实现逐 char 经 UTF32 编码判断（每字符多次字符串分配 + 命中时 Replace 全串重建，
    /// 且代理对两半各自命中——replace 非空时一个 emoji 会输出两份）；
    /// 本实现 rune 单次遍历、无逐字符分配，语义 = 每个 emoji rune 替换为一个 replace。
    /// EnumerateRunes 对无效代理序列产出 U+FFFD rune，天然并入替换。
    /// </summary>
    public static string RemoveEmoji(this string str, string replace = "")
    {
        if (string.IsNullOrEmpty(str))
        {
            return str;
        }
        var sb = new StringBuilder(str.Length);
        var replaced = false;
        Span<char> charBuf = stackalloc char[2];
        foreach (var rune in str.EnumerateRunes())
        {
            // §2-13：U+20000–U+2FFFF 为表意文字补充平面（CJK 扩展 B/C/D 生僻字，如 𠀀），属正常文本不予剔除
            if ((rune.Value == 0xFFFD || rune.Value >= 0x1F000)
                && !(rune.Value >= 0x20000 && rune.Value <= 0x2FFFF))
            {
                sb.Append(replace);
                replaced = true;
            }
            else
            {
                var written = rune.EncodeToUtf16(charBuf);
                sb.Append(charBuf[..written]);
            }
        }
        return replaced ? sb.ToString() : str;
    }

    public static long ToUnix(this DateTime dateTime)
    {
        System.DateTime startTime = TimeZone.CurrentTimeZone.ToLocalTime(new System.DateTime(1970, 1, 1)); // 当地时区
        long timeStamp = (long)(dateTime - startTime).TotalSeconds; // 相差秒数
        return timeStamp;
    }

    /// <summary>
    /// 使用AES加密字符串,按128位处理key
    /// </summary>
    public static string Aes(string content, string key, int seed = 0)
    {
        key = key.Substring(seed, 32);
        var keyArray = Encoding.UTF8.GetBytes(key);
        var toEncryptArray = Encoding.UTF8.GetBytes(content);

        SymmetricAlgorithm des = System.Security.Cryptography.Aes.Create();
        des.Key = keyArray;
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;
        ICryptoTransform cTransform = des.CreateEncryptor();
        byte[] resultArray = cTransform.TransformFinalBlock(toEncryptArray, 0, toEncryptArray.Length);
        var code = Convert.ToBase64String(resultArray);
        code = "A" + seed + "/" + code;
        return code;
    }

    /// <summary>
    /// 复制源文件夹下的所有内容到新文件夹
    /// </summary>
    public static void CopyFolder(string sources, string dest, List<string> allowExtends)
    {
        if (sources.Contains(".git"))
        {
            return;
        }
        if (!Directory.Exists(dest))
        {
            Directory.CreateDirectory(dest);
        }
        DirectoryInfo info = new(sources);
        foreach (FileSystemInfo f in info.GetFileSystemInfos())
        {
            string destName = Path.Combine(dest, f.Name);
            if (f is FileInfo)
            {
                try
                {

                    if (allowExtends == null || allowExtends.Contains(f.Extension))
                    {

                        File.Copy(f.FullName, destName, true);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"copy file {f.FullName} to {destName} error" + e.Message);
                }
            }
            else
            {
                CopyFolder(f.FullName, destName, allowExtends);
            }
        }
    }
}