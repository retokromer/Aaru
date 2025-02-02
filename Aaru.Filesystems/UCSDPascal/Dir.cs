// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : Dir.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : U.C.S.D. Pascal filesystem plugin.
//
// --[ Description ] ----------------------------------------------------------
//
//     Methods to show the U.C.S.D. Pascal catalog as a directory.
//
// --[ License ] --------------------------------------------------------------
//
//     This library is free software; you can redistribute it and/or modify
//     it under the terms of the GNU Lesser General Public License as
//     published by the Free Software Foundation; either version 2.1 of the
//     License, or (at your option) any later version.
//
//     This library is distributed in the hope that it will be useful, but
//     WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//     Lesser General Public License for more details.
//
//     You should have received a copy of the GNU Lesser General Public
//     License along with this library; if not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2023 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using Aaru.CommonTypes.Enums;
using Aaru.CommonTypes.Interfaces;
using Aaru.Helpers;

namespace Aaru.Filesystems;

// Information from Call-A.P.P.L.E. Pascal Disk Directory Structure
public sealed partial class PascalPlugin
{
    /// <inheritdoc />
    public ErrorNumber OpenDir(string path, out IDirNode node)
    {
        node = null;

        if(!_mounted)
            return ErrorNumber.AccessDenied;

        if(!string.IsNullOrEmpty(path) &&
           string.Compare(path, "/", StringComparison.OrdinalIgnoreCase) != 0)
            return ErrorNumber.NotSupported;

        List<string> contents = _fileEntries.Select(ent => StringHandlers.PascalToString(ent.Filename, _encoding)).
                                             ToList();

        if(_debug)
        {
            contents.Add("$");
            contents.Add("$Boot");
        }

        contents.Sort();

        node = new PascalDirNode
        {
            Path      = path,
            _contents = contents.ToArray(),
            _position = 0
        };

        return ErrorNumber.NoError;
    }

    /// <inheritdoc />
    public ErrorNumber ReadDir(IDirNode node, out string filename)
    {
        filename = null;

        if(!_mounted)
            return ErrorNumber.AccessDenied;

        if(node is not PascalDirNode mynode)
            return ErrorNumber.InvalidArgument;

        if(mynode._position < 0)
            return ErrorNumber.InvalidArgument;

        if(mynode._position >= mynode._contents.Length)
            return ErrorNumber.NoError;

        filename = mynode._contents[mynode._position++];

        return ErrorNumber.NoError;
    }

    /// <inheritdoc />
    public ErrorNumber CloseDir(IDirNode node)
    {
        if(node is not PascalDirNode mynode)
            return ErrorNumber.InvalidArgument;

        mynode._position = -1;
        mynode._contents = null;

        return ErrorNumber.NoError;
    }
}