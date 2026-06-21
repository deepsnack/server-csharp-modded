using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.Migration.Migrations;

/// <summary>
/// Moves legacy launcher credentials into the independent password store before removing profile.info.password.
/// </summary>
[Injectable]
public class RemovePassword(PasswordStoreService passwordStoreService) : AbstractProfileMigration
{
    public override string FromVersion
    {
        get { return "~3.11"; }
    }

    public override string ToVersion
    {
        get { return "4.0"; }
    }

    public override string MigrationName
    {
        get { return "RemovePassword-SPTSharp"; }
    }

    public override IEnumerable<Type> PrerequisiteMigrations
    {
        get { return []; }
    }

    public override bool CanMigrate(JsonObject profile, IEnumerable<IProfileMigration> previouslyRanMigrations)
    {
        return profile["info"]?["password"] is not null;
    }

    public override JsonObject? Migrate(JsonObject profile)
    {
        if (profile["info"] is not JsonObject profileInfo || profileInfo["password"] is not JsonValue passwordValue)
        {
            return profile;
        }

        var profileIdValue = profileInfo["id"]?.GetValue<string>();
        var passwordHash = passwordValue.GetValue<string>();
        if (!MongoId.IsValidMongoId(profileIdValue) || !passwordStoreService.ImportLegacyHash(new MongoId(profileIdValue), passwordHash))
        {
            return null;
        }

        profileInfo.Remove("password");
        return base.Migrate(profile);
    }
}
