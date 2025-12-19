using System.Collections.Generic;

namespace TelegramTrayLauncher
{
    internal enum AccountStatus
    {
        Active,
        Frozen,
        Crashed
    }

    internal sealed class AccountState
    {
        public string? GroupName { get; set; }
        public AccountStatus Status { get; set; } = AccountStatus.Active;
    }

    internal sealed class AccountGroup
    {
        public string Name { get; set; } = string.Empty;
    }
}
