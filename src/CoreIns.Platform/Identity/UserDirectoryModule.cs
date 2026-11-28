using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Platform.Identity;

/// <summary>Registration of the user directory (BO-PLT-05).</summary>
internal static class UserDirectoryModule
{
    public static IServiceCollection AddUserDirectory(this IServiceCollection services)
    {
        services.AddScoped<UserDirectoryQueries>();
        services.AddScoped<UserStatusChange>();

        services.AddScoped<IValidator<CreateUser>, CreateUserValidator>();
        services.AddCommandAuditor<CreateUser, UserCreateResponse, CreateUserAuditor>();
        services.AddCommand<CreateUser, UserCreateResponse, CreateUserHandler>(CommandDescriptor.For("plt.User.create"));
        services.AddScoped<IValidator<UpdateUser>, UpdateUserValidator>();
        services.AddCommandAuditor<UpdateUser, UserUpdateResponse, UpdateUserAuditor>();
        services.AddCommand<UpdateUser, UserUpdateResponse, UpdateUserHandler>(CommandDescriptor.For("plt.User.update"));
        services.AddScoped<IValidator<SuspendUser>, SuspendUserValidator>();
        services.AddCommandAuditor<SuspendUser, UserSuspendResponse, SuspendUserAuditor>();
        services.AddCommand<SuspendUser, UserSuspendResponse, SuspendUserHandler>(CommandDescriptor.For("plt.User.suspend"));
        services.AddScoped<IValidator<ReactivateUser>, ReactivateUserValidator>();
        services.AddCommandAuditor<ReactivateUser, UserReactivateResponse, ReactivateUserAuditor>();
        services.AddCommand<ReactivateUser, UserReactivateResponse, ReactivateUserHandler>(CommandDescriptor.For("plt.User.reactivate"));
        services.AddScoped<IValidator<DisableUser>, DisableUserValidator>();
        services.AddCommandAuditor<DisableUser, UserDisableResponse, DisableUserAuditor>();
        services.AddCommand<DisableUser, UserDisableResponse, DisableUserHandler>(CommandDescriptor.For("plt.User.disable"));
        services.AddScoped<IValidator<SetUserLanguage>, SetUserLanguageValidator>();
        services.AddCommandAuditor<SetUserLanguage, UserProfileSetLanguageResponse, SetUserLanguageAuditor>();
        services.AddCommand<SetUserLanguage, UserProfileSetLanguageResponse, SetUserLanguageHandler>(CommandDescriptor.For("plt.UserProfile.setLanguage"));
        services.AddScoped<IValidator<GrantRole>, GrantRoleValidator>();
        services.AddCommandAuditor<GrantRole, RoleAssignmentGrantResponse, GrantRoleAuditor>();
        services.AddCommand<GrantRole, RoleAssignmentGrantResponse, GrantRoleHandler>(CommandDescriptor.For("plt.RoleAssignment.grant"));
        services.AddScoped<IValidator<RevokeRole>, RevokeRoleValidator>();
        services.AddCommandAuditor<RevokeRole, RoleAssignmentRevokeResponse, RevokeRoleAuditor>();
        services.AddCommand<RevokeRole, RoleAssignmentRevokeResponse, RevokeRoleHandler>(CommandDescriptor.For("plt.RoleAssignment.revoke"));

        // A role grant is an approval type with its own authority type (no dimensions): held by Platform.Admin in plt.json.
        services.AddAuthorityType(new AuthorityTypeDefinition(
            AuthorityTypeCode.Parse(UserDirectory.RoleGrantType), ModuleCode.PLT, new LocalizedText("Απόδοση ρόλου", "Role grant"), []));

        services.AddErrorDefinitions(
            ErrorDefinition.For(ModuleCode.PLT, UserErrors.Exists, 409, "Ο χρήστης υπάρχει ήδη", "The user already exists")
                .Describe("Υπάρχει ήδη εγγραφή καταλόγου για αυτή την ταυτότητα Entra.", "A directory entry for this Entra identity already exists."),
            ErrorDefinition.For(ModuleCode.PLT, UserErrors.Suspended, 403, "Ο χρήστης είναι σε αναστολή", "The user is suspended")
                .Describe("Ο λογαριασμός έχει ανασταλεί από διαχειριστή· κάθε κλήση απορρίπτεται μέχρι την επανενεργοποίηση.", "An administrator suspended the user; every call is refused until reactivation."),
            ErrorDefinition.For(ModuleCode.PLT, UserErrors.Disabled, 403, "Ο χρήστης είναι απενεργοποιημένος", "The user is disabled")
                .Describe("Ο λογαριασμός έχει απενεργοποιηθεί οριστικά.", "The user has been disabled for good."),
            ErrorDefinition.For(ModuleCode.PLT, UserErrors.UnknownRole, 422, "Άγνωστος ρόλος", "Unknown role")
                .Describe("Ο ρόλος δεν είναι ρόλος εφαρμογής του καταλόγου δικαιωμάτων.", "The role is not an application role of the permission catalogue."),
            ErrorDefinition.For(ModuleCode.PLT, UserErrors.AssignmentExists, 409, "Ο ρόλος έχει ήδη αποδοθεί", "The role is already assigned")
                .Describe("Ο χρήστης έχει ήδη τον ρόλο στο ίδιο πεδίο ή η έγκριση έχει ήδη χρησιμοποιηθεί.", "The user already holds the role in the same scope, or the approval was already used."),
            ErrorDefinition.For(ModuleCode.PLT, UserErrors.Sod, 403, "Διαχωρισμός καθηκόντων", "Segregation of duties")
                .Describe("Δεν μπορείτε να αλλάξετε τον δικό σας χρήστη ή τους ρόλους σας.", "You cannot change your own user or your own roles."),
            ErrorDefinition.For(ModuleCode.PLT, UserErrors.InvalidTransition, 422, "Μη επιτρεπτή μετάβαση κατάστασης", "The status change is not allowed")
                .Describe("Η κατάσταση του χρήστη ή της απόδοσης ρόλου δεν επιτρέπει αυτή την ενέργεια.", "The status of the user or assignment does not allow this action."));
        return services;
    }
}
