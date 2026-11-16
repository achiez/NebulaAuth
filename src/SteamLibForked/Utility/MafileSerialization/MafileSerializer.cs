using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamLibForked.Models.Session;
using SteamLibForked.Models.SteamIds;

namespace SteamLib.Utility.MafileSerialization;

public partial class MafileSerializer
{
    public const int MAFILE_VERSION = 3;
    public const string SIGNATURE_PROPERTY_NAME = "@SLSV";

    private static readonly HashSet<string> ActualProperties =
        typeof(MobileDataExtended).GetProperties().Select(x => x.Name).ToHashSet();

    public MafileSerializerSettings Settings { get; }

    public MafileSerializer(MafileSerializerSettings? settings = null)
    {
        Settings = settings ?? new MafileSerializerSettings();
    }


    public DeserializedMafileData Deserialize(string json)
    {
        // Decoding bytes with Encoding.GetString keeps the UTF-8 BOM, and Newtonsoft rejects it
        var j = JObject.Parse(json.TrimStart('﻿'));

        var unusedProperties = j.Properties().ToDictionary(x => x.Name, x => x);
        var versionToken = GetToken(j, SIGNATURE_PROPERTY_NAME);
        unusedProperties.Remove(CREDITS_PROPERTY_NAME);
        int? version = null;
        if (versionToken is {Type: JTokenType.Integer})
        {
            version = versionToken.Value<int>();
        }

        unusedProperties.Remove(SIGNATURE_PROPERTY_NAME);
        if (version == MAFILE_VERSION)
        {
            try
            {
                var unused = new Dictionary<string, JProperty>();
                foreach (var (key, prop) in unusedProperties)
                {
                    if (ActualProperties.Contains(key))
                        continue;
                    unused.Add(key, prop);
                }

                var data = j.ToObject<MobileDataExtended>()!;
                data.SessionData = Validate.ValidateMobileData(data, out var sessionResult);

                return DeserializedMafileData.CreateActual(data, unused, sessionResult);
            }
            catch (Exception ex)
            {
                throw new JsonSerializationException(
                    "MafileSerializer found actual 'Version' signature but some error occurred during deserialization",
                    ex);
            }
        }

        var sharedSecretToken = GetTokenOrThrow(j, unusedProperties, nameof(MobileData.SharedSecret), "sharedsecret",
            "shared_secret", "shared");
        var identitySecretToken = GetTokenOrThrow(j, unusedProperties, nameof(MobileData.IdentitySecret),
            "identitysecret", "identity_secret", "identity");
        var deviceId = GetDeviceId(j, Settings, unusedProperties, nameof(MobileData.DeviceId), "deviceid", "device_id",
            "device");

        var sharedSecret = GetBase64(nameof(MobileData.SharedSecret), sharedSecretToken);
        var identitySecret = GetBase64(nameof(MobileData.IdentitySecret), identitySecretToken);
        Validate.NotNullOrEmpty(nameof(MobileData.DeviceId), deviceId);

        var accountName = GetString(j, unusedProperties, nameof(MobileDataExtended.AccountName), "account_name",
            "accountname");
        var revocationCode = GetString(j, unusedProperties, nameof(MobileDataExtended.RevocationCode),
            "revocation_code", "rcode", "r_code", "revocationcode");
        var sessionDataToken = GetToken(j, unusedProperties, nameof(MobileDataExtended.SessionData), "session_data",
            "sessiondata", "session");

        //TODO: Better handling & determination of minified
        var minified
            = string.IsNullOrWhiteSpace(accountName)
              && string.IsNullOrWhiteSpace(revocationCode)
              && (sessionDataToken == null || sessionDataToken.Type == JTokenType.Null);


        if (minified)
        {
            var data = new MobileData
            {
                DeviceId = deviceId,
                IdentitySecret = identitySecret,
                SharedSecret = sharedSecret
            };
            return DeserializedMafileData.Create(data, version, unusedProperties);
        }


        var missingProperties = new List<string>();
        if (string.IsNullOrWhiteSpace(accountName)) missingProperties.Add(nameof(MobileDataExtended.AccountName));
        if (string.IsNullOrWhiteSpace(revocationCode)) missingProperties.Add(nameof(MobileDataExtended.RevocationCode));

        var serverTime = GetLong(j, unusedProperties, nameof(MobileDataExtended.ServerTime), "server_time",
            "servertime");
        var serialNumber = GetSerialNumber(j, Settings, nameof(MobileDataExtended.SerialNumber), unusedProperties,
            "serial_number", "serialnumber");
        var uri = GetString(j, unusedProperties, nameof(MobileDataExtended.Uri), "url", "uri");
        var tokenGid = GetString(j, unusedProperties, nameof(MobileDataExtended.TokenGid), "token_gid", "tokengid");
        var secret1 = GetString(j, unusedProperties, nameof(MobileDataExtended.Secret1), "secret_1", "seecret1");
        var steamId = GetSteamId(j, unusedProperties, nameof(MobileDataExtended.SteamId), "steam_id", "id");

        if (serverTime == null) missingProperties.Add(nameof(MobileDataExtended.ServerTime));
        if (serialNumber == null) missingProperties.Add(nameof(MobileDataExtended.SerialNumber));
        if (string.IsNullOrWhiteSpace(uri)) missingProperties.Add(nameof(MobileDataExtended.Uri));
        if (string.IsNullOrWhiteSpace(tokenGid)) missingProperties.Add(nameof(MobileDataExtended.TokenGid));
        if (string.IsNullOrWhiteSpace(secret1)) missingProperties.Add(nameof(MobileDataExtended.Secret1));

        MobileSessionData? sessionData = null;
        var sResult = DeserializedMafileSessionResult.Missing;
        if (sessionDataToken is JObject sessionObj)
        {
            sessionData = DeserializeMobileSessionData(sessionObj, out sResult, out steamId);
        }

        if ((steamId == null || steamId.Value.Steam64.Id < SteamId64.SEED) &&
            Settings.DeserializationOptions.ThrowIfInvalidSteamId)
        {
            throw new ArgumentException("Can't retrieve SteamId from Mafile", nameof(MobileDataExtended.SteamId));
        }

        if (steamId == null) missingProperties.Add(nameof(MobileDataExtended.SteamId));

        // ReSharper disable once UseObjectOrCollectionInitializer
        var mobileData = new MobileDataExtended
        {
            DeviceId = deviceId,
            IdentitySecret = identitySecret,
            SharedSecret = sharedSecret,
            AccountName = accountName ?? string.Empty,
            RevocationCode = revocationCode,
            ServerTime = serverTime.GetValueOrDefault(),
            SerialNumber = serialNumber.GetValueOrDefault(),
            Uri = uri ?? string.Empty,
            TokenGid = tokenGid ?? string.Empty,
            Secret1 = secret1 ?? string.Empty,
            SessionData = sessionData
        };
        mobileData.SteamId =
            steamId.GetValueOrDefault(); //Keep it here because setting SessionData will override SteamId
        return DeserializedMafileData.Create(mobileData, version, unusedProperties, missingProperties.ToHashSet(),
            sResult);
    }
}