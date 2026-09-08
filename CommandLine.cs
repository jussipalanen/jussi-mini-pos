using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using JussiMiniPos.Models;
using JussiMiniPos.Services;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos;

/// <summary>
/// Command line entry points for working with the catalogue tables without
/// starting the UI:
///
///   JussiMiniPos.exe --seed [--reset]   fill Categories/Products with demo data
///   JussiMiniPos.exe --dump             print what is in those tables
///   JussiMiniPos.exe --clear            delete the catalogue rows
///   JussiMiniPos.exe --ask "..."        run one AI assistant question
///
/// This is a WinExe, so it owns no console. We attach to the terminal that
/// launched it; if there is none, the output goes to a message box instead.
/// </summary>
public static class CommandLine
{
    private const int AttachParentProcess = -1;

    private static readonly StringBuilder Buffer = new();
    private static bool _hasConsole;

    /// <summary>
    /// Runs a command if the arguments name one.
    /// </summary>
    /// <returns>
    /// The process exit code, or <c>null</c> when the app should start normally.
    /// </returns>
    public static int? TryRun(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        // Only the option tokens, never their values. Taking every argument
        // meant a value could pick the command: "--user-add --username v" ran
        // --version and created nothing, and "--lastname Seed" ran the
        // catalogue seeder. A command therefore has to be written with its
        // dashes, which is how every example and the usage text spell it.
        var flags = args
            .Where(IsOption)
            .Select(a => a.TrimStart('-', '/').ToLowerInvariant())
            .ToHashSet();

        if (flags.Overlaps(["version", "v"]))
        {
            // Answered without opening the database: asking what this build is
            // should not depend on the database being usable.
            AttachToTerminal();
            Write($"JussiMiniPos {AppInfo.DisplayVersion}");
            Flush();
            return 0;
        }

        if (flags.Overlaps(["help", "h", "?"]))
        {
            return Run(_ => WriteUsage());
        }

        if (flags.Contains("seed"))
        {
            return Run(database => Seed(database, reset: flags.Contains("reset")));
        }

        if (flags.Contains("dump"))
        {
            return Run(Dump);
        }

        if (flags.Contains("ask"))
        {
            // Blocking is fine here — this runs from OnStartup, before there
            // is a dispatcher loop to keep responsive — but only off the UI
            // thread. Awaiting on it would post the continuation back to a
            // dispatcher that is blocked waiting for the result, and the two
            // would sit there forever. Task.Run leaves that context behind.
            return Run(database =>
                Task.Run(() => Ask(database, Question(args))).GetAwaiter().GetResult());
        }

        if (flags.Contains("users"))
        {
            return Run(ListUsers);
        }

        if (flags.Contains("user-add"))
        {
            return Run(database => AddUser(new UserRepository(database), Options(args)));
        }

        if (flags.Contains("user-update"))
        {
            return Run(database => UpdateUser(new UserRepository(database), Options(args)));
        }

        if (flags.Contains("user-delete"))
        {
            return Run(database => DeleteUser(new UserRepository(database), Options(args)));
        }

        if (flags.Contains("clear"))
        {
            return Run(database =>
            {
                CatalogSeeder.Clear(database);
                Write("Catalogue tables cleared. Sales were left untouched.");
                SweepImages(database);
            });
        }

        return Run(_ =>
        {
            Write($"Unknown option: {string.Join(' ', args)}");
            WriteUsage();
        }, exitCode: 2);
    }

    private static int Run(Action<Database> action, int exitCode = 0)
    {
        AttachToTerminal();

        try
        {
            var database = new Database();
            database.EnsureCreated();
            Write($"Database: {database.Path}");
            Write(string.Empty);

            // Same seeding the application does, so a database set up from a
            // terminal is not left with nobody who can open Admin.
            if (new UserRepository(database).EnsureDefaultAdmin() is { } seeded)
            {
                Write($"Created the administrator \"{UserRepository.DefaultUsername}\" " +
                      $"<{UserRepository.DefaultEmail}> with the first-run password \"{seeded}\".");
                Write("Change it now: --user-update --user admin --password");
                Write(string.Empty);
            }

            action(database);
        }
        catch (OperationCanceledException ex)
        {
            // The user pressed Escape at a prompt. Their own decision, so it
            // is not reported as a failure — but it still exits non-zero, so a
            // script does not read it as the work having been done.
            Write(ex.Message);
            exitCode = 1;
        }
        catch (Exception ex)
        {
            Write($"Failed: {ex.Message}");
            exitCode = 1;
        }

        Flush();
        return exitCode;
    }

    private static void Seed(Database database, bool reset)
    {
        if (CatalogSeeder.HasData(database))
        {
            if (!reset)
            {
                Write("The catalogue tables already hold rows. Nothing was changed.");
                Write("Re-run with --seed --reset to replace them.");
                return;
            }

            CatalogSeeder.Clear(database);
            Write("Existing catalogue rows removed.");
        }

        var result = CatalogSeeder.Seed(database);
        Write($"Seeded {result.Categories} categories, {result.Products} products and " +
              $"{result.Links} product/category links.");

        SweepImages(database);
    }

    /// <summary>
    /// Removes image files nothing points at any more. Dropping catalogue rows
    /// leaves their pictures on disk, so every command that deletes rows sweeps
    /// afterwards.
    /// </summary>
    private static void SweepImages(Database database)
    {
        var store = new ImageStore(database);
        var removed = store.DeleteUnreferenced(new CatalogRepository(database).GetAllImagePaths());

        if (removed > 0)
        {
            Write($"Removed {removed} unreferenced image file(s) from {store.RootPath}.");
        }
    }

    /// <summary>
    /// Runs one question through the AI assistant and prints what came back.
    /// This is how the Gemini setup gets checked without starting the till:
    /// it shows the model and whether a key was found, what the search made of
    /// the question, how many catalogue rows the model was given, and the
    /// products it picked. A missing key or a rejected one shows up as the
    /// notice line rather than as a failure, exactly as it does in the UI.
    /// </summary>
    private static async Task Ask(Database database, string question)
    {
        var options = new OptionsRepository(database);
        var settings = AiSettings.Load(options, System.IO.Path.GetDirectoryName(database.Path));

        Write($"Model:   {settings.Model}");
        Write($"API key: {(settings.HasApiKey ? "found" : "missing")}");
        Write($"Enabled: {(settings.IsEnabled ? "yes" : "no - switched off in Admin")}");
        Write(string.Empty);

        if (question.Length == 0)
        {
            Write("""Nothing to ask. Try: --ask "Mitä sopii kahvin kanssa?" """);
            return;
        }

        var terms = ProductSearch.Parse(question);
        Write($"Question: {question}");
        Write($"Stems:    {(terms.Stems.Count == 0 ? "(none)" : string.Join(", ", terms.Stems))}");

        if (terms.MaxPrice is { } max)
        {
            Write($"Ceiling:  {max.ToString("C", CultureInfo.CurrentCulture)}");
        }

        Write(string.Empty);

        var assistant = new ShoppingAssistant(database, new CatalogRepository(database), options);
        var answer = await assistant.AskAsync(question);

        if (answer.Notice is { } notice)
        {
            Write($"! {notice}");
            Write(string.Empty);
        }

        Write($"Retrieved {answer.CandidateCount} candidate product(s) for the model.");
        Write(string.Empty);
        Write(answer.Reply);
        Write(string.Empty);

        if (answer.Suggestions.Count == 0)
        {
            Write("  (no products suggested)");
            return;
        }

        foreach (var suggestion in answer.Suggestions)
        {
            Write($"  {suggestion.Product.Id}  " +
                  $"{suggestion.Product.EffectivePrice.ToString("C", CultureInfo.CurrentCulture),9}  " +
                  $"{suggestion.Product.Name}");

            if (suggestion.Reason.Length > 0)
            {
                Write($"          {suggestion.Reason}");
            }
        }
    }

    /// <summary>
    /// The words that are not options, which is where --ask keeps its
    /// question. Quoting it is the caller's job, as with any shell argument.
    /// </summary>
    private static string Question(string[] args) =>
        string.Join(' ', args.Where(a => !a.StartsWith('-') && !a.StartsWith('/')));

    // ---------- Users ----------

    private static void ListUsers(Database database)
    {
        using var connection = database.OpenConnection();

        // PasswordHash is deliberately not selected: it is a hash rather than
        // a password, but printing it hands a copy to anybody watching the
        // terminal for no benefit.
        Write("Users");
        WriteTable(connection,
            """
            SELECT Id, Username, Email, TRIM(FirstName || ' ' || LastName) AS Name, Role
            FROM Users
            ORDER BY Id;
            """);
    }

    private static void AddUser(UserRepository users, IReadOnlyDictionary<string, string> options)
    {
        var username = Required(options, "username");
        var email = Required(options, "email");
        var role = ParseRole(Required(options, "role"));
        var password = Password(options, "New password: ", out var generated);

        // Checked here as well as by the UNIQUE constraint, so the failure is
        // a sentence rather than a SQLite error about an index.
        if (users.FindUser(username) is { } byName)
        {
            throw new InvalidOperationException(
                $"Username \"{username}\" is taken by user #{byName.Id}.");
        }

        if (users.FindUser(email) is { } byEmail)
        {
            throw new InvalidOperationException(
                $"Email \"{email}\" is taken by user #{byEmail.Id}.");
        }

        var firstName = Value(options, "firstname") ?? string.Empty;
        var lastName = Value(options, "lastname") ?? string.Empty;

        var id = users.InsertUser(username, email, password, role, firstName, lastName);
        Write($"Added user #{id}  {username} <{email}>  role={UserRoleNames.ToStorage(role)}");

        if (generated)
        {
            WriteGenerated(password);
        }
    }

    private static void UpdateUser(UserRepository users, IReadOnlyDictionary<string, string> options)
    {
        var selector = Required(options, "user");
        var user = users.FindUser(selector)
            ?? throw new InvalidOperationException($"No user matched \"{selector}\".");

        var username = Value(options, "username") ?? user.Username;
        var email = Value(options, "email") ?? user.Email;
        var role = Value(options, "role") is { } roleText ? ParseRole(roleText) : user.Role;
        var firstName = Value(options, "firstname") ?? user.FirstName;
        var lastName = Value(options, "lastname") ?? user.LastName;

        // --generate-password is the "reset it for someone who forgot theirs"
        // case: nobody has to invent a password, and it is printed once.
        var generating = options.ContainsKey("generate-password");
        var changingPassword = generating || options.ContainsKey("password");

        if (username == user.Username
            && email == user.Email
            && role == user.Role
            && firstName == user.FirstName
            && lastName == user.LastName
            && !changingPassword)
        {
            throw new InvalidOperationException(
                "Nothing to change. Pass --username, --email, --firstname, --lastname, " +
                "--role, --password or --generate-password.");
        }

        // Losing the last administrator would leave Admin unreachable with no
        // way back short of editing the database by hand.
        if (user.Role == UserRole.Admin && role != UserRole.Admin && users.CountAdmins() <= 1)
        {
            throw new InvalidOperationException(
                $"{user.Username} is the only administrator. Promote another user first.");
        }

        if (!string.Equals(username, user.Username, StringComparison.OrdinalIgnoreCase)
            && users.FindUser(username) is { } byName)
        {
            throw new InvalidOperationException(
                $"Username \"{username}\" is taken by user #{byName.Id}.");
        }

        if (!string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase)
            && users.FindUser(email) is { } byEmail)
        {
            throw new InvalidOperationException(
                $"Email \"{email}\" is taken by user #{byEmail.Id}.");
        }

        // The password is read before anything is written, so cancelling the
        // prompt leaves the user exactly as it was.
        string? password = null;
        if (generating)
        {
            password = PasswordGenerator.Generate();
        }
        else if (changingPassword)
        {
            password = Password(options, "New password: ", out _);
        }

        users.UpdateUser(user.Id, username, email, role);

        if (firstName != user.FirstName || lastName != user.LastName)
        {
            users.SetName(user.Id, firstName, lastName);
        }

        if (password is not null)
        {
            users.SetPassword(user.Id, password);
        }

        Write($"Updated user #{user.Id}  {username} <{email}>  role={UserRoleNames.ToStorage(role)}");

        if (firstName.Length > 0 || lastName.Length > 0)
        {
            Write($"Name: {$"{firstName} {lastName}".Trim()}");
        }

        if (password is not null)
        {
            Write("Password changed.");
        }

        if (generating)
        {
            WriteGenerated(password!);
        }
    }

    private static void DeleteUser(UserRepository users, IReadOnlyDictionary<string, string> options)
    {
        var selector = Required(options, "user");
        var user = users.FindUser(selector)
            ?? throw new InvalidOperationException($"No user matched \"{selector}\".");

        if (user.Role == UserRole.Admin && users.CountAdmins() <= 1)
        {
            throw new InvalidOperationException(
                $"{user.Username} is the only administrator. Add another one first.");
        }

        users.DeleteUser(user.Id);
        Write($"Deleted user #{user.Id}  {user.Username} <{user.Email}>");

        if (users.Count() == 0)
        {
            Write($"No users left. The next start seeds \"{UserRepository.DefaultUsername}\" again.");
        }
    }

    /// <summary>
    /// A role name, rejecting anything unrecognised. Deliberately stricter
    /// than <see cref="UserRoleNames.FromStorage"/>, which is reading rows
    /// that already exist: here a typo is a mistake to report, not a value to
    /// interpret.
    /// </summary>
    private static UserRole ParseRole(string value) =>
        Enum.TryParse<UserRole>(value, ignoreCase: true, out var role)
            ? role
            : throw new InvalidOperationException(
                $"Unknown role \"{value}\". Use admin, manager or seller.");

    /// <summary>
    /// The password to store. Three ways in, so a script, a person and an
    /// unattended run all have one that suits:
    ///   --password "..."   use exactly this
    ///   --password         type it at a prompt, without it being echoed
    ///   (omitted)          generate one and print it once
    /// </summary>
    /// <param name="generated">
    /// Set when the password was made up here, so the caller can print it —
    /// nobody can look it up afterwards.
    /// </param>
    private static string Password(
        IReadOnlyDictionary<string, string> options,
        string prompt,
        out bool generated)
    {
        if (!options.ContainsKey("password"))
        {
            generated = true;
            return PasswordGenerator.Generate();
        }

        generated = false;

        var password = Value(options, "password") ?? ReadHiddenLine(prompt);

        if (password.Length < PasswordHasher.MinimumLength)
        {
            throw new InvalidOperationException(
                $"The password must be at least {PasswordHasher.MinimumLength} characters.");
        }

        return password;
    }

    /// <summary>
    /// Prints a generated password, with the warning that goes with it. Only
    /// the hash is stored, so this is the one time it can be read.
    /// </summary>
    private static void WriteGenerated(string password)
    {
        Write(string.Empty);
        Write($"Generated password: {password}");
        Write("Write it down now — only its hash is stored, so it cannot be shown again.");
        Write("Pass --password to choose one yourself, or --password with no value to be prompted.");
    }

    /// <summary>
    /// Reads a line without echoing it, so a password does not stay on screen.
    /// Falls back to a normal read when the console cannot be read key by key,
    /// which is what happens when input is redirected.
    /// </summary>
    private static string ReadHiddenLine(string prompt)
    {
        if (!_hasConsole)
        {
            throw new InvalidOperationException(
                "There is no console to prompt on. Pass the password with --password \"...\".");
        }

        Console.Write(prompt);

        var password = new StringBuilder();

        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password.Length--;
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.Escape)
                {
                    // Its own type, so the catch below cannot mistake a
                    // deliberate cancellation for redirected input and drop
                    // into an echoing read.
                    throw new OperationCanceledException("Cancelled; nothing was changed.");
                }

                if (!char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Console.ReadKey throws this when input is redirected, and only
            // on the first key, so nothing has been collected yet. Read it as
            // a plain line instead; that echoes, which is the caller's problem
            // to avoid by piping.
            Console.WriteLine();
            return (Console.ReadLine() ?? string.Empty).Trim();
        }

        Console.WriteLine();
        return password.ToString();
    }

    /// <summary>
    /// An option's value, or null when it was not given — and "  " counts as
    /// not given. Trimmed before the emptiness test, not after: the other way
    /// round, --username " " satisfied Required() as "" and inserted an
    /// account with a blank username that nothing could log in as.
    /// </summary>
    private static string? Value(IReadOnlyDictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out var value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        Value(options, name)
        ?? throw new InvalidOperationException($"Missing --{name}. See --help.");

    /// <summary>
    /// Reads <c>--name value</c> pairs. An option with nothing after it — or
    /// followed by another option — is recorded with an empty value, which is
    /// how <c>--password</c> on its own comes to mean "ask me".
    /// </summary>
    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!IsOption(args[i]))
            {
                continue;
            }

            var name = args[i].TrimStart('-', '/');
            var hasValue = i + 1 < args.Length && !IsOption(args[i + 1]);

            options[name] = hasValue ? args[i + 1] : string.Empty;

            if (hasValue)
            {
                i++;
            }
        }

        return options;
    }

    private static bool IsOption(string arg) =>
        arg.StartsWith('-') || arg.StartsWith('/');

    private static void Dump(Database database)
    {
        using var connection = database.OpenConnection();

        Write("Options");
        WriteTable(connection, "SELECT Id, OptionName, OptionValue FROM Options ORDER BY OptionName;");

        // PasswordHash is deliberately not selected. It is a hash rather than
        // a password, but printing it to a terminal still hands a copy to
        // anybody watching, for no benefit.
        Write("Users");
        WriteTable(connection, "SELECT Id, Username, Email, Role FROM Users ORDER BY Id;");

        Write("Categories");
        WriteTable(connection,
            """
            SELECT c.Id, c.Title, COALESCE(p.Title, '-') AS Parent, c.IsPublic AS Public,
                   (SELECT COUNT(*) FROM ProductCategories pc WHERE pc.CategoryId = c.Id) AS Products
            FROM Categories c
            LEFT JOIN Categories p ON p.Id = c.ParentId
            ORDER BY COALESCE(c.ParentId, c.Id), c.Id;
            """);

        Write("Products");

        // Columns named *Cents are rendered as euros by WriteTable.
        WriteTable(connection,
            """
            SELECT p.Id, p.Title,
                   p.PriceCents,
                   p.SalePriceCents,
                   p.IsPublic AS Public,
                   (SELECT GROUP_CONCAT(c.Id || '=' || c.Title, ', ')
                      FROM ProductCategories pc
                      JOIN Categories c ON c.Id = pc.CategoryId
                     WHERE pc.ProductId = p.Id) AS Categories,
                   (SELECT COUNT(*) FROM ProductImages i WHERE i.ProductId = p.Id) AS Images
            FROM Products p
            ORDER BY p.Id;
            """);
    }

    /// <summary>Prints a query as an aligned text table.</summary>
    private static void WriteTable(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();

        // Money is stored as cents; show it as euros and drop the suffix from
        // the heading, so PriceCents reads as "Price  2,50 €".
        var isMoney = names.Select(n => n.EndsWith("Cents", StringComparison.Ordinal)).ToArray();
        var headers = names
            .Select((n, i) => isMoney[i] ? n[..^"Cents".Length] : n)
            .ToArray();

        var rows = new List<string[]>();

        while (reader.Read())
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i =>
                reader.IsDBNull(i)
                    ? isMoney[i] ? "-" : string.Empty
                    : isMoney[i]
                        ? SalesRepository.FromCents(reader.GetInt64(i)).ToString("C", CultureInfo.CurrentCulture)
                        : reader.GetValue(i).ToString() ?? string.Empty)]);
        }

        if (rows.Count == 0)
        {
            Write("  (empty - run --seed first)");
            Write(string.Empty);
            return;
        }

        var widths = headers
            .Select((header, i) => rows.Select(r => r[i].Length).Append(header.Length).Max())
            .ToArray();

        string Line(IReadOnlyList<string> cells) =>
            "  " + string.Join("  ", cells.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd();

        Write(Line(headers));
        Write("  " + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            Write(Line(row));
        }

        Write(string.Empty);
        Write($"  {rows.Count} row(s)");
        Write(string.Empty);
    }

    private static void WriteUsage()
    {
        Write("JussiMiniPos - catalogue tools");
        Write(string.Empty);
        Write("  JussiMiniPos.exe                 start the application");
        Write("  JussiMiniPos.exe --seed          fill Categories/Products with demo data");
        Write("  JussiMiniPos.exe --seed --reset  replace any existing catalogue rows");
        Write("  JussiMiniPos.exe --dump          print the catalogue tables");
        Write("  JussiMiniPos.exe --clear         delete the catalogue rows (keeps sales)");
        Write($"""  JussiMiniPos.exe --ask "..."     ask the AI assistant one question""");
        Write("  JussiMiniPos.exe --users         list the users");
        Write("  JussiMiniPos.exe --version       print the version and exit");
        Write("  JussiMiniPos.exe --help          this text");
        Write(string.Empty);
        Write("Users. Roles are admin, manager or seller; only admin can open the Admin view.");
        Write("--user takes a username or an email. Passwords: give --password a value to set");
        Write("one, pass --password with no value to be prompted without it reaching your");
        Write("shell history, or leave it out entirely and one is generated and printed once.");
        Write(string.Empty);
        Write("  JussiMiniPos.exe --user-add --username matti --email matti@example.com \\");
        Write("                   --role seller --password \\");
        Write("                   --firstname Matti --lastname Meikäläinen");
        Write("  JussiMiniPos.exe --user-update --user matti --role manager");
        Write("  JussiMiniPos.exe --user-add --username liisa --email liisa@example.com \\");
        Write("                   --role manager            (password is generated)");
        Write("  JussiMiniPos.exe --user-update --user matti --firstname Matti");
        Write("  JussiMiniPos.exe --user-update --user matti --generate-password");
        Write("  JussiMiniPos.exe --user-update --user matti --password \"NewPass1234!\"");
        Write("  JussiMiniPos.exe --user-delete --user matti");
        Write(string.Empty);
        Write("The last administrator cannot be deleted or demoted, since that would leave");
        Write("the Admin view unreachable.");
        Write(string.Empty);
        Write("The AI assistant is configured in the application's Admin view: on/off and the");
        Write($"model go to the Options table, and the key to a {AiSettings.KeyFileName} file next to the");
        Write($"database. {AiSettings.ApiKeyVariable} and {AiSettings.ModelVariable} are used when nothing is stored");
        Write($"(default model {AiSettings.DefaultModel}).");
    }

    private static void Write(string line)
    {
        if (_hasConsole)
        {
            Console.WriteLine(line);
        }
        else
        {
            Buffer.AppendLine(line);
        }
    }

    /// <summary>Shows the buffered output when there was no terminal to print to.</summary>
    private static void Flush()
    {
        if (_hasConsole || Buffer.Length == 0)
        {
            return;
        }

        System.Windows.MessageBox.Show(Buffer.ToString(), "JussiMiniPos");
        Buffer.Clear();
    }

    private static void AttachToTerminal()
    {
        _hasConsole = AttachConsole(AttachParentProcess);
        if (_hasConsole)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
