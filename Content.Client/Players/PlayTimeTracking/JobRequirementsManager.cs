﻿using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Content.Shared.CCVar;
using Content.Shared.Players;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Robust.Client;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Players.PlayTimeTracking;

public sealed class JobRequirementsManager
{
    [Dependency] private readonly IBaseClient _client = default!;
    [Dependency] private readonly IClientNetManager _net = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    private readonly Dictionary<string, TimeSpan> _roles = new();
    private bool _whitelisted = false;
    private readonly List<string> _roleBans = new();

    private ISawmill _sawmill = default!;

    public event Action? Updated;

    public void Initialize()
    {
        _sawmill = Logger.GetSawmill("job_requirements");

        // Yeah the client manager handles role bans and playtime but the server ones are separate DEAL.
        _net.RegisterNetMessage<MsgRoleBans>(RxRoleBans);
        _net.RegisterNetMessage<MsgPlayTime>(RxPlayTime);
        _net.RegisterNetMessage<MsgWhitelist>(RxWhitelist);

        _client.RunLevelChanged += ClientOnRunLevelChanged;
    }

    private void ClientOnRunLevelChanged(object? sender, RunLevelChangedEventArgs e)
    {
        if (e.NewLevel == ClientRunLevel.Initialize)
        {
            // Reset on disconnect, just in case.
            _roles.Clear();
        }
    }

    private void RxRoleBans(MsgRoleBans message)
    {
        _sawmill.Debug($"Received roleban info containing {message.Bans.Count} entries.");

        if (_roleBans.Equals(message.Bans))
            return;

        _roleBans.Clear();
        _roleBans.AddRange(message.Bans);
        Updated?.Invoke();
    }

    private void RxPlayTime(MsgPlayTime message)
    {
        _roles.Clear();

        // NOTE: do not assign _roles = message.Trackers due to implicit data sharing in integration tests.
        foreach (var (tracker, time) in message.Trackers)
        {
            _roles[tracker] = time;
        }

        /*var sawmill = Logger.GetSawmill("play_time");
        foreach (var (tracker, time) in _roles)
        {
            sawmill.Info($"{tracker}: {time}");
        }*/
        Updated?.Invoke();
    }

    private void RxWhitelist(MsgWhitelist message)
    {
        _whitelisted = message.Whitelisted;
    }

    public bool IsAllowed(JobPrototype job, [NotNullWhen(false)] out FormattedMessage? reason)
    {
        reason = null;
        if (_roleBans.Contains($"Job:{job.ID}"))
        {
            reason = FormattedMessage.FromUnformatted(Loc.GetString("role-ban"));
            return false;
        }

        if (job.Requirements == null ||
            !_cfg.GetCVar(CCVars.GameRoleTimers))
        {
            return true;
        }

        var player = _playerManager.LocalPlayer?.Session;
        if (player == null)
            return true;

        return CheckRoleTime(job.Requirements, job.WhitelistRequired, out reason);
    }

    public bool CheckRoleTime(HashSet<JobRequirement>? requirements, bool whitelistRequired, [NotNullWhen(false)] out FormattedMessage? reason)
    {
        reason = null;

        if (requirements == null)
            return true;

        var reasons = new List<string>();
        foreach (var requirement in requirements)
        {
            if (JobRequirements.TryRequirementMet(requirement, _roles, out var jobReason, _entManager, _prototypes))
                continue;

            reasons.Add(jobReason.ToMarkup());
        }

        if (whitelistRequired && _cfg.GetCVar(CCVars.WhitelistEnabled) && !_whitelisted)
        {
            if (reasons.Count > 0)
                reasons.Add("\n");

            reasons.Add(Loc.GetString("playtime-deny-reason-not-whitelisted"));
        }

        reason = reasons.Count == 0 ? null : FormattedMessage.FromMarkup(string.Join('\n', reasons));
        return reason == null;
    }

    public bool IsWhitelisted()
    {
        return _whitelisted;
    }
}
