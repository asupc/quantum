using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quantum.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "t_ai_conversation",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    AllowEnvValues = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastMessageTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_ai_conversation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_ai_message",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: true),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: true),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    RunId = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_ai_message", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_ai_model",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    ContextWindow = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxOutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    Temperature = table.Column<double>(type: "REAL", nullable: false),
                    Enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Sort = table.Column<int>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_ai_model", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_ai_proposal",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: true),
                    MessageId = table.Column<string>(type: "TEXT", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    BaseHash = table.Column<string>(type: "TEXT", nullable: true),
                    NewContent = table.Column<string>(type: "TEXT", nullable: true),
                    NewHash = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    Diagnostics = table.Column<string>(type: "TEXT", nullable: true),
                    TestStatus = table.Column<string>(type: "TEXT", nullable: true),
                    TestRunLogId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    AppliedVersionId = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppliedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_ai_proposal", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_ai_provider",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Protocol = table.Column<string>(type: "TEXT", nullable: true),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    UsePlatformProxy = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: false),
                    Enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Sort = table.Column<int>(type: "INTEGER", nullable: false),
                    SupportsTools = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTestTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastTestOk = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastTestMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_ai_provider", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_ai_run",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: true),
                    TriggerType = table.Column<string>(type: "TEXT", nullable: true),
                    TargetFile = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    Rounds = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_ai_run", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_ai_setting",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxRounds = table.Column<int>(type: "INTEGER", nullable: false),
                    RunTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    LogTailLines = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoAnalyzeOnFailure = table.Column<bool>(type: "INTEGER", nullable: false),
                    TestRunNotifyMode = table.Column<string>(type: "TEXT", nullable: true),
                    TestRunRequireConfirm = table.Column<bool>(type: "INTEGER", nullable: false),
                    KeepVersionsPerFile = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveFullPrompt = table.Column<bool>(type: "INTEGER", nullable: false),
                    SystemPromptExtra = table.Column<string>(type: "TEXT", nullable: true),
                    AllowScriptDelete = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowTaskManage = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowEnvManage = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowCustomDataManage = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_ai_setting", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_ai_step",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: true),
                    Seq = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Arguments = table.Column<string>(type: "TEXT", nullable: true),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_ai_step", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_app_device",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    Platform = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_app_device", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_app_file",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    Ext = table.Column<string>(type: "TEXT", nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_app_file", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_app_notification",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: true),
                    Jump = table.Column<string>(type: "TEXT", nullable: true),
                    MsgId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_app_notification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_app_notify_setting",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TaskPush = table.Column<bool>(type: "INTEGER", nullable: false),
                    SystemPush = table.Column<bool>(type: "INTEGER", nullable: false),
                    SecurityPush = table.Column<bool>(type: "INTEGER", nullable: false),
                    DndStart = table.Column<string>(type: "TEXT", nullable: true),
                    DndEnd = table.Column<string>(type: "TEXT", nullable: true),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_app_notify_setting", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_app_refresh_token",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    RevokedReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_app_refresh_token", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_bookmark",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    IconUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ParentId = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsFolder = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_bookmark", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_chat_message",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", nullable: true),
                    ContentText = table.Column<string>(type: "TEXT", nullable: true),
                    MsgId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionKey = table.Column<string>(type: "TEXT", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_chat_message", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_chat_session",
                columns: table => new
                {
                    SessionKey = table.Column<string>(type: "TEXT", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    LastReadSeq = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_chat_session", x => x.SessionKey);
                });

            migrationBuilder.CreateTable(
                name: "t_command",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    CommunicationType = table.Column<int>(type: "INTEGER", nullable: true),
                    Enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableRegex = table.Column<bool>(type: "INTEGER", nullable: false),
                    MessageType = table.Column<int>(type: "INTEGER", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_command", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_custom_data",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: true),
                    Data1 = table.Column<string>(type: "TEXT", nullable: true),
                    Data2 = table.Column<string>(type: "TEXT", nullable: true),
                    Data3 = table.Column<string>(type: "TEXT", nullable: true),
                    Data4 = table.Column<string>(type: "TEXT", nullable: true),
                    Data5 = table.Column<string>(type: "TEXT", nullable: true),
                    Data6 = table.Column<string>(type: "TEXT", nullable: true),
                    Data7 = table.Column<string>(type: "TEXT", nullable: true),
                    Data8 = table.Column<string>(type: "TEXT", nullable: true),
                    Data9 = table.Column<string>(type: "TEXT", nullable: true),
                    Data10 = table.Column<string>(type: "TEXT", nullable: true),
                    Data11 = table.Column<string>(type: "TEXT", nullable: true),
                    Data12 = table.Column<string>(type: "TEXT", nullable: true),
                    Data13 = table.Column<string>(type: "TEXT", nullable: true),
                    Data14 = table.Column<string>(type: "TEXT", nullable: true),
                    Data15 = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_custom_data", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_custom_data_title",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: true),
                    TypeName = table.Column<string>(type: "TEXT", nullable: true),
                    Title1 = table.Column<string>(type: "TEXT", nullable: true),
                    Title2 = table.Column<string>(type: "TEXT", nullable: true),
                    Title3 = table.Column<string>(type: "TEXT", nullable: true),
                    Title4 = table.Column<string>(type: "TEXT", nullable: true),
                    Title5 = table.Column<string>(type: "TEXT", nullable: true),
                    Title6 = table.Column<string>(type: "TEXT", nullable: true),
                    Title7 = table.Column<string>(type: "TEXT", nullable: true),
                    Title8 = table.Column<string>(type: "TEXT", nullable: true),
                    Title9 = table.Column<string>(type: "TEXT", nullable: true),
                    Title10 = table.Column<string>(type: "TEXT", nullable: true),
                    Title11 = table.Column<string>(type: "TEXT", nullable: true),
                    Title12 = table.Column<string>(type: "TEXT", nullable: true),
                    Title13 = table.Column<string>(type: "TEXT", nullable: true),
                    Title14 = table.Column<string>(type: "TEXT", nullable: true),
                    Title15 = table.Column<string>(type: "TEXT", nullable: true),
                    Hide = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_custom_data_title", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_env",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    Enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_env", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_log",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Operator = table.Column<string>(type: "TEXT", nullable: true),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserIP = table.Column<string>(type: "TEXT", nullable: true),
                    LogType = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectoryName = table.Column<string>(type: "TEXT", nullable: true),
                    LogPath = table.Column<string>(type: "TEXT", nullable: true),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Module = table.Column<string>(type: "TEXT", nullable: true),
                    RequestPath = table.Column<string>(type: "TEXT", nullable: true),
                    Exception = table.Column<string>(type: "TEXT", nullable: true),
                    ElapsedMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_menu",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Component = table.Column<string>(type: "TEXT", nullable: true),
                    ParentName = table.Column<string>(type: "TEXT", nullable: true),
                    Sort = table.Column<int>(type: "INTEGER", nullable: false),
                    HideInMenu = table.Column<bool>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Icon = table.Column<string>(type: "TEXT", nullable: true),
                    HideInBread = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_menu", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_opentriggertask",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Secret = table.Column<string>(type: "TEXT", nullable: false),
                    SrciptFile = table.Column<string>(type: "TEXT", nullable: true),
                    HttpMethod = table.Column<string>(type: "TEXT", nullable: true),
                    Envs = table.Column<string>(type: "TEXT", nullable: true),
                    Enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Whitelist = table.Column<string>(type: "TEXT", nullable: true),
                    EnablePush = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableProxy = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_opentriggertask", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_script_version",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    Hash = table.Column<string>(type: "TEXT", nullable: true),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    Size = table.Column<int>(type: "INTEGER", nullable: false),
                    LineCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    Creator = table.Column<string>(type: "TEXT", nullable: true),
                    TaskId = table.Column<string>(type: "TEXT", nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_script_version", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_task",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Command = table.Column<string>(type: "TEXT", nullable: true),
                    CommandEnv = table.Column<string>(type: "TEXT", nullable: true),
                    Cron = table.Column<string>(type: "TEXT", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    TextToPicture = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableRegex = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    DayLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    EnablePush = table.Column<bool>(type: "INTEGER", nullable: false),
                    PushGroup = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revocation = table.Column<bool>(type: "INTEGER", nullable: false),
                    Manager = table.Column<bool>(type: "INTEGER", nullable: false),
                    WaitTime = table.Column<int>(type: "INTEGER", nullable: false),
                    CreateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TaskStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskThreadId = table.Column<string>(type: "TEXT", nullable: true),
                    TaskStartNotify = table.Column<string>(type: "TEXT", nullable: true),
                    TaskEndNotify = table.Column<string>(type: "TEXT", nullable: true),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    CommunicationTypes = table.Column<string>(type: "TEXT", nullable: true),
                    EnableProxy = table.Column<bool>(type: "INTEGER", nullable: false),
                    SessionName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_task", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_task_sub",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    EnableRegex = table.Column<bool>(type: "INTEGER", nullable: false),
                    Command = table.Column<string>(type: "TEXT", nullable: true),
                    Sort = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: true),
                    CommandEnv = table.Column<string>(type: "TEXT", nullable: true),
                    Revocation = table.Column<bool>(type: "INTEGER", nullable: false),
                    WaitTime = table.Column<int>(type: "INTEGER", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_task_sub", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_t_ai_message_ConversationId_Seq",
                table: "t_ai_message",
                columns: new[] { "ConversationId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_t_ai_model_ProviderId",
                table: "t_ai_model",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_t_ai_proposal_Status",
                table: "t_ai_proposal",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_t_ai_step_RunId_Seq",
                table: "t_ai_step",
                columns: new[] { "RunId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_t_app_file_CreateTime",
                table: "t_app_file",
                column: "CreateTime");

            migrationBuilder.CreateIndex(
                name: "IX_t_app_notification_CreatedAt",
                table: "t_app_notification",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_t_chat_message_Seq",
                table: "t_chat_message",
                column: "Seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_chat_message_SessionKey_Seq",
                table: "t_chat_message",
                columns: new[] { "SessionKey", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_t_custom_data_Type",
                table: "t_custom_data",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_t_log_CreateTime",
                table: "t_log",
                column: "CreateTime");

            migrationBuilder.CreateIndex(
                name: "IX_t_opentriggertask_Secret",
                table: "t_opentriggertask",
                column: "Secret");

            migrationBuilder.CreateIndex(
                name: "IX_t_script_version_FileName_CreateTime",
                table: "t_script_version",
                columns: new[] { "FileName", "CreateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_t_task_sub_TaskId",
                table: "t_task_sub",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "t_ai_conversation");

            migrationBuilder.DropTable(
                name: "t_ai_message");

            migrationBuilder.DropTable(
                name: "t_ai_model");

            migrationBuilder.DropTable(
                name: "t_ai_proposal");

            migrationBuilder.DropTable(
                name: "t_ai_provider");

            migrationBuilder.DropTable(
                name: "t_ai_run");

            migrationBuilder.DropTable(
                name: "t_ai_setting");

            migrationBuilder.DropTable(
                name: "t_ai_step");

            migrationBuilder.DropTable(
                name: "t_app_device");

            migrationBuilder.DropTable(
                name: "t_app_file");

            migrationBuilder.DropTable(
                name: "t_app_notification");

            migrationBuilder.DropTable(
                name: "t_app_notify_setting");

            migrationBuilder.DropTable(
                name: "t_app_refresh_token");

            migrationBuilder.DropTable(
                name: "t_bookmark");

            migrationBuilder.DropTable(
                name: "t_chat_message");

            migrationBuilder.DropTable(
                name: "t_chat_session");

            migrationBuilder.DropTable(
                name: "t_command");

            migrationBuilder.DropTable(
                name: "t_custom_data");

            migrationBuilder.DropTable(
                name: "t_custom_data_title");

            migrationBuilder.DropTable(
                name: "t_env");

            migrationBuilder.DropTable(
                name: "t_log");

            migrationBuilder.DropTable(
                name: "t_menu");

            migrationBuilder.DropTable(
                name: "t_opentriggertask");

            migrationBuilder.DropTable(
                name: "t_script_version");

            migrationBuilder.DropTable(
                name: "t_task");

            migrationBuilder.DropTable(
                name: "t_task_sub");
        }
    }
}
