/// <summary>
/// A stand-in for signing in, so that the sample has callers with names and a reviewer who is not
/// one of them. <b>A demonstration, and not a pattern to copy</b>: it takes whoever a link says the
/// visitor is, with no password and no proof.
/// </summary>
/// <remarks>
/// Everything the disclosure audit records, it records under a name — who was sent an answer, and who
/// later read the record of it. A real application has those names already: they come from its
/// authentication, and <see cref="ScryOptions.Caller"/> and the explorer's reviewer read them off the
/// authenticated principal. This puts a principal there by the shortest route available, so that the
/// sample can show the audit without also being a sample of authentication.
/// </remarks>
static class DemoSignIn
{
    const string cookie = "scry-demo-user";

    /// <summary>The one name the sample lets read the record. Everybody else only appears in it.</summary>
    public const string Reviewer = "auditor";

    /// <summary>Who the sample offers to be: two people who ask questions, and the one who reads what they were sent.</summary>
    public static IReadOnlyList<string> Names { get; } = ["alice", "bob", Reviewer];

    /// <summary>Whoever the cookie says, as the request's user. Nobody where there is no cookie.</summary>
    public static void UseDemoSignIn(this WebApplication app) =>
        app.Use(
            (context, next) =>
            {
                if (context.Request.Cookies.TryGetValue(cookie, out var name) &&
                    Valid(name))
                {
                    context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "Demo"));
                }

                return next(context);
            });

    public static void MapDemoSignIn(this WebApplication app)
    {
        // A link that makes the visitor whoever it says. Which is the whole of what is wrong with it.
        app.MapGet(
            "/demo/sign-in",
            (HttpContext context, string @as) =>
            {
                if (!Valid(@as))
                {
                    return Results.BadRequest("A demo name is a few letters, digits, dots or dashes.");
                }

                context.Response.Cookies.Append(
                    cookie,
                    @as,
                    new()
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict
                    });
                return Results.Redirect("/");
            });
        app.MapGet(
            "/demo/sign-out",
            (HttpContext context) =>
            {
                context.Response.Cookies.Delete(cookie);
                return Results.Redirect("/");
            });
        app.MapGet("/", (HttpContext context) => Results.Content(Home(context.User.Identity?.Name), "text/html"));
    }

    public static bool IsReviewer(HttpContext context) =>
        context.User.Identity?.Name == Reviewer;

    // Short and plain, since it is written into a cookie and onto a page.
    static bool Valid(string? name) =>
        name is {Length: > 0 and <= 40} &&
        name.All(_ => char.IsAsciiLetterOrDigit(_) || _ is '.' or '-');

    static string Home(string? name)
    {
        var who = "nobody. Every answer is recorded under a name, so a query asked now is refused";
        if (name is not null)
        {
            who = $"<b>{WebUtility.HtmlEncode(name)}</b> (<a href=\"/demo/sign-out\">sign out</a>)";
        }

        var links = string.Join(" · ", Names.Select(_ => $"<a href=\"/demo/sign-in?as={_}\">{_}</a>"));
        return $$"""
                 <!DOCTYPE html>
                 <html lang="en">
                 <head>
                     <meta charset="utf-8" />
                     <title>Scry disclosure sample</title>
                     <link rel="icon" href="data:," />
                 </head>
                 <body>
                     <h1>Scry disclosure sample</h1>
                     <p>You are {{who}}.</p>
                     <p>
                         Be somebody: {{links}}.
                         <small>A demonstration of having a name, not of signing in. There is no password.</small>
                     </p>
                     <ol>
                         <li><a href="/scry">Ask something</a> in the query explorer. Whatever is answered is recorded under your name before it is sent.</li>
                         <li>Become <a href="/demo/sign-in?as={{Reviewer}}">{{Reviewer}}</a> and <a href="/scry-disclosures/">see who was sent what</a>. Reading the record is recorded too.</li>
                     </ol>
                 </body>
                 </html>
                 """;
    }
}
