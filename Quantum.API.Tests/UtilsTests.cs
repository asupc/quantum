using Quantum.Entities.Model;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

public class RegexHelperTests
{
    [Theory]
    [InlineData("abc")]          // 字母开头，最短合法
    [InlineData("a1_B")]
    [InlineData("User001")]
    public void Code_ValidCodesPass(string code)
    {
        Assert.True(RegexHelper.Code(code));
    }

    [Theory]
    [InlineData("1abc")]         // 数字开头
    [InlineData("_abc")]         // 下划线开头
    [InlineData("a")]            // 少于 2 位
    [InlineData("a b")]          // 含空格
    [InlineData("")]             // 空
    public void Code_InvalidCodesFail(string code)
    {
        Assert.False(RegexHelper.Code(code));
    }
}

public class RandomStringBuilderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(32)]
    [InlineData(64)]
    public void Create_ReturnsRequestedLength(int length)
    {
        Assert.Equal(length, RandomStringBuilder.Create(length).Length);
    }

    [Fact]
    public void Create_DefaultLengthIs32()
    {
        Assert.Equal(32, RandomStringBuilder.Create().Length);
    }

    [Fact]
    public void Create_ContainsOnlyAlphanumeric()
    {
        var s = RandomStringBuilder.Create(200);
        Assert.Matches("^[a-zA-Z0-9]+$", s);
    }

    [Fact]
    public void Create_GeneratesDistinctStrings()
    {
        var set = new HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            set.Add(RandomStringBuilder.Create(16));
        }
        Assert.Equal(20, set.Count);
    }
}

public class BaseModelTests
{
    private class Entity : BaseModel { }

    [Fact]
    public void DefaultId_Is32CharUpperCaseNoDashes()
    {
        var id = new Entity().Id;
        Assert.Equal(32, id.Length);
        Assert.DoesNotContain("-", id);
        Assert.Equal(id, id.ToUpper());
    }

    [Fact]
    public void DefaultId_IsUniquePerInstance()
    {
        Assert.NotEqual(new Entity().Id, new Entity().Id);
    }

    [Fact]
    public void NewId_Is32CharLowercaseNoDashes()
    {
        var id = new Entity().NewId();
        Assert.Equal(32, id.Length);
        Assert.DoesNotContain("-", id);
        Assert.Equal(id, id.ToLower());
    }
}

// ==================================================================== MySQL 连接串字符集归一（2026-09-20 生产 AI 修复流程事故）

public class MySqlCharsetNormalizationTests
{
    [Theory]
    [InlineData("Server=s;Database=d;CharSet=utf8;Uid=u", "Server=s;Database=d;CharSet=utf8mb4;Uid=u")]
    [InlineData("Server=s;Database=d;charset=utf8mb3;Uid=u", "Server=s;Database=d;charset=utf8mb4;Uid=u")]
    [InlineData("Server=s;Database=d;CharSet=utf8mb4;Uid=u", "Server=s;Database=d;CharSet=utf8mb4;Uid=u")]       // 已是 mb4 不动
    [InlineData("Server=s;Database=d;CharSet=UTF8;Uid=u", "Server=s;Database=d;CharSet=utf8mb4;Uid=u")]          // 大小写保留
    [InlineData("Server=s;Database=d;Uid=u", "Server=s;Database=d;Uid=u;CharSet=utf8mb4")]                       // 缺省追加
    [InlineData("Server=s;Database=d;Uid=u;", "Server=s;Database=d;Uid=u;CharSet=utf8mb4")]                     // 尾分号去重
    [InlineData("", "")]                                                                                          // 空串直通
    public void NormalizeCharsetToUtf8mb4_Cases(string input, string expected)
    {
        Assert.Equal(expected, Quantum.Data.QuantumMySqlDbContext.NormalizeCharsetToUtf8mb4(input));
    }
}

