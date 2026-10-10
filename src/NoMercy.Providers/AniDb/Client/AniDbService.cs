// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using System.Security;
using AniDB;
using AniDB.RequestEnums;
using AniDB.ResponseItems;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.AniDb.Models;
using Serilog.Events;

namespace NoMercy.Providers.AniDb.Client;

public class AniDbService : IAniDbService
{
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(5);

    private string _username = "";
    private string _password = "";
    private SecureString? _apiKey;

    private readonly AniDBClient? _client;
    private readonly Func<bool> _isConnected;
    private readonly Action<Action> _logout;
    private readonly Action _disconnect;
    private readonly TimeSpan _logoutTimeout;

    private AniDBClient Client =>
        _client ?? throw new InvalidOperationException("AniDB client is not available.");

    public AniDbService()
    {
        _client = new(
            new()
            {
                ClientName = "nomercy",
                ClientVersion = 1,
                LocalPort = (ushort)(RuntimeServerSettings.Current.ExternalServerPort + 1),
            }
        );
        _isConnected = () => _client.IsConnected;
        _logout = callback => _client.Logout(_ => callback());
        _disconnect = _client.Disconnect;
        _logoutTimeout = LogoutTimeout;

        UserPass? userPass = CredentialManager.Credential("AniDb");
        if (userPass == null)
            return;

        _username = userPass.Username;
        _password = userPass.Password;

        if (userPass.ApiKey == null)
            return;

        _apiKey = CredentialManager.ConvertToSecureString(userPass.ApiKey);
    }

    internal AniDbService(
        Func<bool> isConnected,
        Action<Action> logout,
        Action disconnect,
        TimeSpan logoutTimeout
    )
    {
        _isConnected = isConnected;
        _logout = logout;
        _disconnect = disconnect;
        _logoutTimeout = logoutTimeout;
    }

    public void SetCredentials(string username, string password, string? apiKey)
    {
        _username = username;
        _password = password;

        if (apiKey == null)
            return;

        _apiKey = CredentialManager.ConvertToSecureString(apiKey);
    }

    public Task Init()
    {
        // Run on the thread pool so the connect/login finishes off-thread.
        return Task.Run(() =>
        {
            try
            {
                Client.Connect();
                Client.Login(LoginCallback, _username, _password, _apiKey);
            }
            catch (Exception e)
            {
                Logger.AniDb(e.Message, LogEventLevel.Fatal);
                throw;
            }
        });
    }

    public async Task<AniDBAnimeItem> GetRandomAnime(CancellationToken ct = default)
    {
        TaskCompletionSource<AniDBAnimeItem> tcs = new();

        Client.FetchRandomAnime(
            response =>
            {
                Logger.AniDb(response.StatusCode.ToString());
                Logger.AniDb(response.StatusMessage);

                response.GetMessageItem(
                    0,
                    new AniDbCallbackObject<AniDBAnimeItem>(messageItem =>
                    {
                        messageItem.parseContentsDefault();
                        tcs.SetResult(messageItem);
                    })
                );
            },
            RandomAnimeSource.ANY,
            2
        );

        // Guard against the AniDB UDP callback never firing: time out after 10s
        // (linked to the caller's token) instead of awaiting the task forever.
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        return await tcs.Task.WaitAsync(cts.Token);
    }

    private static void LoginCallback(AniDBMessageResponse message)
    {
        Logger.AniDb(message, LogEventLevel.Debug);
    }

    public void Dispose()
    {
        if (_isConnected())
            LogoutAndDisconnect(_logout, _disconnect, _logoutTimeout);

        GC.SuppressFinalize(this);
    }

    private static void LogoutAndDisconnect(
        Action<Action> logout,
        Action disconnect,
        TimeSpan timeout
    )
    {
        TaskCompletionSource<bool> completed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        try
        {
            logout(() => completed.TrySetResult(true));
            completed.Task.Wait(timeout);
        }
        catch (Exception)
        {
            // A failed logout must not prevent shutdown from disconnecting.
        }
        finally
        {
            disconnect();
        }
    }
}
