// Copyright (c) The University of Dundee 2026
// This file is part of the RDMP linked-server cohort SQL plugin.
// Licensed under the GNU General Public License v3 or later, as RDMP itself. See <https://www.gnu.org/licenses/>.

using System.Runtime.CompilerServices;

// GenerateAssemblyInfo is off in HICPlugin (versions come from SharedAssemblyInfo.cs), so the SDK's
// <InternalsVisibleTo> item would be ignored; declare it here.
[assembly: InternalsVisibleTo("HICPluginTests")]
