// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

namespace RdmpLinkedServerCohortSql;

/// <summary>
/// Thrown when the plugin cannot write SQL for a cohort. <see cref="Reason"/> says which kind of problem it is, for code and
/// tests. <see cref="Problem"/> says what is wrong and <see cref="Fix"/> what to do, each in full sentences.
/// <see cref="Subject"/> names what the problem is in, such as a set or a table. The message names the subject once and
/// never shows the reason code.
/// </summary>
public sealed class LinkedServerRenderException : Exception
{
    public LinkedServerRenderException(LinkedServerRenderReason reason, string problem, string fix = null, string subject = null)
        : base(Compose(problem, fix, subject))
    {
        Reason = reason;
        Problem = problem;
        Fix = fix;
        Subject = subject;
    }

    public LinkedServerRenderReason Reason { get; }
    public string Problem { get; }
    public string Fix { get; }
    public string Subject { get; }

    /// <summary>What is wrong and what to do, without the subject.</summary>
    public string Detail => Fix == null ? Problem : Problem + " " + Fix;

    /// <summary>The same problem, now naming <paramref name="subject"/>, unless it already names a subject.</summary>
    internal LinkedServerRenderException About(string subject) => Subject != null ? this : new(Reason, Problem, Fix, subject);

    private static string Compose(string problem, string fix, string subject)
    {
        var detail = fix == null ? problem : problem + " " + fix;
        return subject == null ? detail : $"Cannot write linked-server SQL for {subject}. {detail}";
    }
}
