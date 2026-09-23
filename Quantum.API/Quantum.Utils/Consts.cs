namespace Quantum.Utils;

/// <summary>
/// JWT 相关共享密钥的进程内快照（§1-8）：四字段收敛到一个不可变 Snapshot，读取走单次 Volatile.Read，
/// 生产热刷新走 SetJwtSecrets 单次换入——消除旧实现「四个静态字段分四次赋值、读者可能读到跨字段撕裂」的窗口。
/// 为兼容既有读写点（含测试直接赋值 / 元组解构赋值），保留逐字段可写属性；但生产多字段刷新须用 SetJwtSecrets。
/// </summary>
public static class Consts
{
    private sealed class Snapshot
    {
        public readonly string SymmetricSecurityKey;
        public readonly string SecurityAudience;
        public readonly string SecurityIssuer;
        public readonly long ManagerTokenNotBefore;

        public Snapshot(string key, string audience, string issuer, long notBefore)
        {
            SymmetricSecurityKey = key;
            SecurityAudience = audience;
            SecurityIssuer = issuer;
            ManagerTokenNotBefore = notBefore;
        }
    }

    private static Snapshot _snap = new(null, null, null, 0);

    public static string SymmetricSecurityKey
    {
        get => Volatile.Read(ref _snap).SymmetricSecurityKey;
        set
        {
            var cur = Volatile.Read(ref _snap);
            Volatile.Write(ref _snap, new Snapshot(value, cur.SecurityAudience, cur.SecurityIssuer, cur.ManagerTokenNotBefore));
        }
    }

    public static string SecurityAudience
    {
        get => Volatile.Read(ref _snap).SecurityAudience;
        set
        {
            var cur = Volatile.Read(ref _snap);
            Volatile.Write(ref _snap, new Snapshot(cur.SymmetricSecurityKey, value, cur.SecurityIssuer, cur.ManagerTokenNotBefore));
        }
    }

    public static string SecurityIssuer
    {
        get => Volatile.Read(ref _snap).SecurityIssuer;
        set
        {
            var cur = Volatile.Read(ref _snap);
            Volatile.Write(ref _snap, new Snapshot(cur.SymmetricSecurityKey, cur.SecurityAudience, value, cur.ManagerTokenNotBefore));
        }
    }

    /// <summary>
    /// 管理令牌签发下限（Unix 秒，与 Setting.ManagerTokenNotBefore 同步刷新）：
    /// 改密后作废旧 Manager 令牌的吊销闸（JwtTokenValidator/JwtBearer 共用）。
    /// </summary>
    public static long ManagerTokenNotBefore
    {
        get => Volatile.Read(ref _snap).ManagerTokenNotBefore;
        set
        {
            var cur = Volatile.Read(ref _snap);
            Volatile.Write(ref _snap, new Snapshot(cur.SymmetricSecurityKey, cur.SecurityAudience, cur.SecurityIssuer, value));
        }
    }

    /// <summary>生产热刷新入口：单次换入四字段快照，避免读者看到跨字段撕裂。</summary>
    public static void SetJwtSecrets(string symmetricSecurityKey, string securityAudience, string securityIssuer, long managerTokenNotBefore)
    {
        Volatile.Write(ref _snap, new Snapshot(symmetricSecurityKey, securityAudience, securityIssuer, managerTokenNotBefore));
    }
}
