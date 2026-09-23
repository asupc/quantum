using System.Text.RegularExpressions;

namespace Quantum.Utils;

public static class RegexHelper
{
    /// <summary>
    /// 验证只能包含数字和字母
    /// </summary>
    /// <returns></returns>
    public static bool Code(string code)
    {
        Regex regex = new("^[a-zA-Z][a-zA-Z0-9_]{1,64}$");
        return regex.Match(code).Success;
    }
}
