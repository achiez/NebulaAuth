using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using NebulaAuth.Core;
using NebulaAuth.Model.Entities;
using SteamLib.Exceptions.Authorization;

namespace NebulaAuth.Model.MAAC;

public static class MAACRequestHandler
{
    private const string LOC_PATH = "MAAC";
    private static readonly ConcurrentDictionary<Mafile, PortableMaClientErrorData> _errors = new();

    public static void Register(Mafile mafile)
    {
        _errors[mafile] = new PortableMaClientErrorData();
    }

    public static void Unregister(Mafile mafile)
    {
        _errors.TryRemove(mafile, out _);
        MaacErrorLog.Instance.Forget(mafile.AccountName);
    }

    public static PortableMaClientStatus ClearErrors(Mafile mafile)
    {
        if (_errors.TryGetValue(mafile, out var data))
        {
            data.Clear();
        }

        return PortableMaClientStatus.Ok();
    }

    /// <summary>
    ///     Handles a MAAC request with progressive error handling strategy: retries and status management based on error
    ///     history.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="client"></param>
    /// <param name="func"></param>
    /// <returns></returns>
    public static async Task<T> HandleRequest<T>(PortableMaClient client, Func<Task<T>> func)
    {
        var result = await client.DoRequest(func, client.Mafile.PreviouslyHadNoErrors());
        if (result.IsSuccess)
        {
            InformRequestSuccessful(client.Mafile);
            return result.Data;
        }

        if (client.Mafile.PreviouslyHadNoErrors())
        {
            client.SetStatus(PortableMaClientStatus.Warning(GetPortableMaClientStatus("SessionError")));
        }
        else if (client.Mafile.LastErrorWasAtLeast(Settings.Instance.MaacRetryInterval))
        {
            Shell.Logger.Info("Retrying MAAC request for {name} MAAC account after previous error.",
                client.Mafile.AccountName);
            result = await client.DoRequest(func);
            if (result.IsSuccess)
            {
                InformRequestSuccessful(client.Mafile);
                return result.Data;
            }
        }

        if (client.Mafile.ErrorPersistedFor(Settings.Instance.MaacErrorThreshold))
        {
            client.SetStatus(PortableMaClientStatus.Error(GetPortableMaClientStatus("SessionError")));
        }

        AddError(client.Mafile, result.Exception!);
        throw result.Exception!;
    }


    private static async Task<Result<T>> DoRequest<T>(this PortableMaClient client, Func<Task<T>> req,
        bool withSessionHandler = true)
    {
        try
        {
            return withSessionHandler
                ? Result<T>.Success(await SessionHandler.Handle(req, client.Mafile, client.Chp(),
                    GetTimerPrefix(client.Mafile), false))
                : Result<T>.Success(await req());
        }
        catch (SessionInvalidException ex)
        {
            Shell.Logger.Warn("SessionInvalidException caught in MAAC request handler.");
            return Result<T>.Error(ex);
        }
    }

    private static void InformRequestSuccessful(Mafile mafile)
    {
        if (_errors.TryGetValue(mafile, out var data) && data.Clear())
        {
            Shell.Logger.Info("MAAC request for {name} MAAC account succeeded, error history cleared",
                mafile.AccountName);
        }

        var client = mafile.LinkedClient;
        if (client != null && client.Status.StatusType != PortableMaClientStatusType.Ok)
        {
            client.SetStatus(PortableMaClientStatus.Ok());
        }
    }

    private static void AddError(Mafile mafile, Exception ex)
    {
        if (_errors.TryGetValue(mafile, out var data))
        {
            Shell.Logger.Info("Registering error for {name} MAAC account: {ex}", mafile.AccountName, ex.Message);
            data.AddEntry(ex);
        }
    }

    private static bool ErrorPersistedFor(this Mafile mafile, TimeSpan span)
    {
        if (_errors.TryGetValue(mafile, out var data))
        {
            var oldestError = data.GetOldestErrorTime();
            if (oldestError.HasValue && DateTime.UtcNow - oldestError.Value > span)
            {
                return true;
            }
        }

        return false;
    }


    private static bool PreviouslyHadNoErrors(this Mafile mafile)
    {
        if (_errors.TryGetValue(mafile, out var data))
        {
            return data.NoErrors;
        }

        return true;
    }

    private static bool LastErrorWasAtLeast(this Mafile mafile, TimeSpan timeAgo)
    {
        if (!_errors.TryGetValue(mafile, out var data)) return false;
        var latestError = data.GetTimeFromLastError();
        if (latestError.HasValue && latestError.Value > timeAgo)
        {
            return true;
        }

        return false;
    }

    public static bool IsReady(Mafile maf)
    {
        if (maf.LinkedClient is {Status.StatusType: PortableMaClientStatusType.Ok}) return true;
        return maf.LinkedClient is {Status.StatusType: PortableMaClientStatusType.Warning}
               && maf.LastErrorWasAtLeast(Settings.Instance.MaacRetryInterval);
    }

    private static string GetTimerPrefix(Mafile mafile)
    {
        return GetLocalization("TimerPrefix") + mafile.AccountName + ": ";
    }

    private static string GetLocalization(string key)
    {
        return LocManager.GetCodeBehindOrDefault(key, LOC_PATH, key);
    }

    private static string GetPortableMaClientStatus(string key)
    {
        return LocManager.GetCodeBehindOrDefault(key, LOC_PATH, "PortableMaClientStatus", key);
    }
}