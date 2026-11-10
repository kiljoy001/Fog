namespace NinePSharp.Fog.Gefs;

// user.c's table, parsed from /adm/users as parseusers parses it: a line of id:name:leader:members
// for each user and group, # starting a comment. A line is read only to its newline, as readline
// reads it. Fog's files name their users rather than numbering them, so ingroup finds both by name.
public sealed class Users
{
    private const string Esyntax = "syntax error";
    private const string Enouser = "user does not exist";

    private readonly User[] users;

    private Users(User[] users) => this.users = users;

    // The table gefs reams for the user who reams it, adm's member and the leader of its own group.
    public static string Ream(string user) => $"-1:adm::{user}\n0:none::\n1:{user}:{user}:\n";

    // parseusers: each line's id and name first, and then each leader and member by name, its
    // faults written to log a line at a time; a table with a fault is refused with gefs's error.
    public static Users Parse(string udata, TextWriter log)
    {
        string[] lines = Lines(udata);
        var users = new List<User>();
        for (int lnum = 1; lnum <= lines.Length; lnum++)
        {
            string p = lines[lnum - 1];
            if (p.Length == 0 || p[0] == '#')
            {
                continue;
            }

            // getfield gives the id even without a ':' after it, and then no name.
            string[] fields = p.Split(':');
            if (fields.Length < 2)
            {
                throw Fault(log, lnum, "missing ':' after name", Esyntax);
            }

            users.Add(new User(Atol(fields[0]), fields[1]));
        }

        int i = 0;
        for (int lnum = 1; lnum <= lines.Length; lnum++)
        {
            string p = lines[lnum - 1];
            if (p.Length == 0 || p[0] == '#')
            {
                continue;
            }

            string[] fields = p.Split(':');
            if (fields.Length < 3)
            {
                throw Fault(log, lnum, "missing ':' after name", Esyntax);
            }

            // A leader must be a user; Fog has no group leaders yet, so it is not kept.
            if (fields[2].Length > 0 && !users.Exists(u => u.Name == fields[2]))
            {
                throw Fault(log, lnum, $"leader {fields[2]} does not exist", Enouser);
            }

            if (fields.Length < 4)
            {
                throw new GefsException(Esyntax);
            }

            foreach (string m in fields[3].Split(','))
            {
                if (m.Length > 0)
                {
                    users[i].Members.Add((users.FindLast(u => u.Name == m) ?? throw Fault(log, lnum, $"user {m} does not exist", Enouser)).Id);
                }
            }

            i++;
        }

        return new Users([.. users]);
    }

    // ingroup: a user is in a group that is itself or that names it a member.
    public bool InGroup(string user, string group)
    {
        User? u = Find(user), g = Find(group);
        return u is not null && g is not null && (u.Id == g.Id || g.Members.Contains(u.Id));
    }

    // The lines readline gives: each up to its newline, and none after the last newline.
    private static string[] Lines(string udata)
    {
        string[] lines = udata.Split('\n');
        return lines[..^1];
    }

    private static GefsException Fault(TextWriter log, int lnum, string what, string error)
    {
        log.Write($"/adm/users:{lnum}: {what}\n");
        return new GefsException(error);
    }

    // atol: blanks, an optional sign and the digits after it, and 0 for none.
    private static int Atol(string s)
    {
        s = s.TrimStart(' ', '\t');
        int n = 0;
        for (int j = s.StartsWith('-') || s.StartsWith('+') ? 1 : 0; j < s.Length && char.IsAsciiDigit(s[j]); j++)
        {
            n = (n * 10) + (s[j] - '0');
        }

        return s.StartsWith('-') ? -n : n;
    }

    // name2user: the first user of the name, as the table is searched from its start.
    private User? Find(string name) => Array.Find(users, u => u.Name == name);

    private sealed class User(int id, string name)
    {
        public int Id { get; } = id;

        public string Name { get; } = name;

        public List<int> Members { get; } = [];
    }
}
