// ============================================================
//  Services/App/MasterConnectionCoordinator.cs
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace ShockUI.Services.App;

/// <summary>
/// Treats the System Controller module as the master connection point.
/// When the System Controller goes online, the registered follower
/// modules (Camera, VisNIR, SWIR, Pan/Tilt) are automatically connected
/// using whatever port each has currently selected. When the System
/// Controller goes offline, all followers are disconnected.
///
/// The coordinator does not own the connections — each module still has
/// its own SerialService and port. The coordinator just invokes each
/// module's existing Connect / Disconnect command, sequentially, so the
/// operator only has to click "Connect" once on the System Controller.
/// </summary>
public sealed class MasterConnectionCoordinator
{
    private readonly List<Follower> _followers = new();

    /// <summary>
    /// Re-entrancy guard. A follower's ConnectCommand may itself raise
    /// PropertyChanged on its IsConnected, which we don't want to
    /// re-cascade. The flag stays set for the duration of an outer cascade.
    /// </summary>
    private bool _cascadeInProgress;

    private sealed record Follower(
        string Name,
        IRelayCommand ConnectCmd,
        IRelayCommand DisconnectCmd,
        Func<bool> IsConnectedGetter);

    /// <summary>
    /// Register a follower module's Connect / Disconnect commands. The
    /// IsConnected getter lets us skip modules that are already in the
    /// desired state, so a partial-state cascade doesn't toggle anything
    /// unnecessarily.
    /// </summary>
    public void RegisterFollower(
        string name,
        IRelayCommand connectCmd,
        IRelayCommand disconnectCmd,
        Func<bool> isConnectedGetter)
    {
        _followers.Add(new Follower(name, connectCmd, disconnectCmd, isConnectedGetter));
    }

    /// <summary>
    /// Connect every registered follower that isn't already connected.
    /// Failures are logged via Debug and don't abort the cascade — one
    /// flaky module shouldn't prevent the others from coming online.
    /// </summary>
    public async Task ConnectFollowersAsync()
    {
        if (_cascadeInProgress) return;
        _cascadeInProgress = true;
        try
        {
            foreach (var f in _followers)
            {
                if (f.IsConnectedGetter()) continue;
                if (!f.ConnectCmd.CanExecute(null)) continue;

                try
                {
                    if (f.ConnectCmd is IAsyncRelayCommand asyncCmd)
                        await asyncCmd.ExecuteAsync(null);
                    else
                        f.ConnectCmd.Execute(null);

                    Debug.WriteLine($"[MasterConnectionCoordinator] {f.Name} connected (cascade).");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MasterConnectionCoordinator] {f.Name} cascade-connect failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _cascadeInProgress = false;
        }
    }

    /// <summary>Disconnect every connected follower.</summary>
    public void DisconnectFollowers()
    {
        if (_cascadeInProgress) return;
        _cascadeInProgress = true;
        try
        {
            foreach (var f in _followers)
            {
                if (!f.IsConnectedGetter()) continue;

                try
                {
                    f.DisconnectCmd.Execute(null);
                    Debug.WriteLine($"[MasterConnectionCoordinator] {f.Name} disconnected (cascade).");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MasterConnectionCoordinator] {f.Name} cascade-disconnect failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _cascadeInProgress = false;
        }
    }
}