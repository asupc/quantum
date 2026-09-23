namespace Quantum.Utils;

/// <summary>
/// 确定性认证拒绝（区别于一般业务失败）：ExceptionFilter 映射为信封 Code=401。
/// 语义：客户端收到 401 必须清除本地会话跳登录页；500 仍表示临时故障可重试。
/// 目前仅 App 刷新令牌拒绝使用（AppAuthService.RefreshAsync），勿滥用于普通鉴权失败
/// （那是 CustomAuthorizationFilter/ManagerOnlyFilter 的职责）。
/// </summary>
public class UnauthorizedBusinessException : BusinessException
{
    public UnauthorizedBusinessException(string message) : base(message)
    {
    }
}
