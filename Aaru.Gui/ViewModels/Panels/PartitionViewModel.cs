// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : PartitionViewModel.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : GUI view models.
//
// --[ Description ] ----------------------------------------------------------
//
//     View model and code for the partition information panel.
//
// --[ License ] --------------------------------------------------------------
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU General public License as
//     published by the Free Software Foundation, either version 3 of the
//     License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General public License for more details.
//
//     You should have received a copy of the GNU General public License
//     along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2023 Natalia Portillo
// ****************************************************************************/

using Aaru.CommonTypes;

namespace Aaru.Gui.ViewModels.Panels;

public sealed class PartitionViewModel
{
    public PartitionViewModel(Partition partition)
    {
        NameText  = string.Format(Localization.Core.Partition_name_0, partition.Name);
        TypeText  = string.Format(Localization.Core.Partition_type_0, partition.Type);
        StartText = string.Format(Localization.Core.Partition_start_sector_0_byte_1, partition.Start, partition.Offset);

        LengthText = string.Format(Localization.Core.Partition_length_0_sectors_1_bytes, partition.Length,
                                   partition.Size);

        DescriptionLabelText = Localization.Core.Title_Partition_description;
        DescriptionText      = partition.Description;
    }

    public string NameText             { get; }
    public string TypeText             { get; }
    public string StartText            { get; }
    public string LengthText           { get; }
    public string DescriptionLabelText { get; }
    public string DescriptionText      { get; }
}