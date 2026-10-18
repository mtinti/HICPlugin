// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NUnit.Framework;

namespace Rdmp.LinkedServerCohortSql.Tests;

public sealed class DependencyBoundaryTests
{
    [Test]
    public void PluginContainsNoDataConnectionOrCredentialCalls()
    {
        using var stream = File.OpenRead(typeof(LinkedServerCohortSqlBuilder).Assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var forbiddenTypes = new[] { "DataAccessPortal", "DiscoveredServer", "DiscoveredDatabase", "DiscoveredTable",
            "DbConnection", "DataAccessCredentials", "EncryptedString", "DataAccessPointCollection", "SelfCertifyingDataAccessPoint" };
        var types = metadata.TypeReferences.Select(h => metadata.GetString(metadata.GetTypeReference(h).Name)).ToArray();
        Assert.That(types.Intersect(forbiddenTypes), Is.Empty);
        var members = metadata.MemberReferences.Select(h => metadata.GetString(metadata.GetMemberReference(h).Name)).ToArray();
        Assert.That(members.Intersect(new[] { "GetCredentialsIfExists", "Discover", "get_DataAccessCredentials", "ShowData" }), Is.Empty);
    }
}
