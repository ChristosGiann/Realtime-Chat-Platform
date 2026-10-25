using Chat.Api.Identity;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Profiles;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Api.Registration;

[ApiController]
[Route("api/auth")]
public sealed class RegistrationController(
    UserManager<ApplicationUser> users,
    ChatDbContext db) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<RegistrationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegistrationRequest request, CancellationToken cancellationToken)
    {
        var userName = request.UserName!.Trim();
        var email = request.Email!.Trim();
        var displayName = request.DisplayName is null ? userName : request.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            return Failure(new Dictionary<string, string[]> { ["DisplayName"] = ["Display name cannot be blank."] });

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var user = new ApplicationUser { UserName = userName, Email = email };
            var result = await users.CreateAsync(user, request.Password!);
            if (!result.Succeeded)
            {
                var errors = result.Errors.GroupBy(error => Field(error.Code))
                    .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray());
                var conflict = result.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName");
                return Failure(errors, conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
            }

            db.Profiles.Add(new UserProfile { UserId = user.Id, DisplayName = displayName });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return StatusCode(StatusCodes.Status201Created,
                new RegistrationResponse(user.Id, userName, email, displayName));
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "EmailIndex" or "UserNameIndex"
            })
        {
            // The database remains the final arbiter when concurrent requests race.
            await transaction.RollbackAsync(cancellationToken);
            var postgres = (PostgresException)exception.InnerException!;
            var field = postgres.ConstraintName == "EmailIndex" ? "Email" : "UserName";
            return Failure(new Dictionary<string, string[]> { [field] = ["This value is already in use."] },
                StatusCodes.Status409Conflict);
        }
    }

    private ActionResult Failure(Dictionary<string, string[]> errors,
        int status = StatusCodes.Status400BadRequest) =>
        new ObjectResult(new ValidationProblemDetails(errors)
        {
            Status = status,
            Title = status == StatusCodes.Status409Conflict
                ? "An account with these credentials already exists."
                : "Registration validation failed."
        });

    private static string Field(string code) => code switch
    {
        "DuplicateEmail" or "InvalidEmail" => "Email",
        "DuplicateUserName" or "InvalidUserName" => "UserName",
        _ when code.StartsWith("Password", StringComparison.Ordinal) => "Password",
        _ => "Registration"
    };
}
