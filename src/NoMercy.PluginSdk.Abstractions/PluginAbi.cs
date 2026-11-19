// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

namespace NoMercy.PluginSdk.Abstractions;

public static class PluginAbi
{
    // 12.1 added IPluginMusicQuery.GetFailedDjAnalysisAsync, PluginTrackDjFailure and the
    // PluginTopics.MusicAnalysisCompleted topic. Additive for every plugin already loading.
    // 12.2 added PluginViewRequest.Origin.
    // 12.3 added IPluginStorageScope.MarkSparseAsync and IPluginMediaProxy.MintFileAsync.
    // Additive: both are default members, so a plugin or test double that implements
    // either interface itself keeps compiling.
    // 12.4 added IPluginStorage.MarkSparseAsync(localPath), a default member too.
    // 12.5 added IPluginPortMap.ProbeAsync and PluginPortReachability. Default member.
    // 12.6 added IPluginContext.Lyrics (IPluginLyrics, the lyricsWrite hook) and
    // Lyric.Words. Default member.
    // 12.7 added IPluginAudioTools.RenderAsync with PluginAudioRender, PluginAudioSource,
    // PluginAudioOutput, PluginAudioCodec and PluginAudioRenderedFile, plus
    // PluginAudioInput.PluginFile, PluginAudioRunOptions.StallTimeout and
    // PluginAudioRunResult.Outputs. Additive: the member is a default member, and the three
    // existing records grew a property each, not a constructor parameter, so the
    // constructors a plugin was compiled against are still there.
    // 12.8 added IPluginMusicQuery.GetTracksByIdAsync (a default member),
    // PluginLyricsText.IsComplete and AskAgainAfter (two init-only properties, so every
    // new PluginLyricsText(candidates) written before them still means a complete answer)
    // and PluginFilterGraphTokens.AlignModel (a new constant).
    // 12.9 added PluginIndexSignature, the optional signature fields of PluginRepositoryEntry
    // and PluginVersionEntry, and an optional indexSignature parameter on
    // IPluginManager.InstallPluginArchiveAsync (a default member no plugin calls).
    // 12.10 added PluginCapabilities.Lan (PluginLanCapability, checked by PluginLanRoutes): the
    // routes a plugin serves to a device on the owner's network that cannot sign in. Additive:
    // an optional manifest field, absent on every plugin built before it. It also added
    // PluginLanCaller and PluginControllerBase.LanCaller (the device a LAN route answers), a new
    // record and a new protected property. No member changed shape: a link minted through
    // IPluginMediaProxy while a LAN route serves a device is bound to that device.
    // PluginLanCaller.ToString leaves BaseAddress out, because it carries the credential.
    // 12.10 also added PluginFilterGraphTokens.StemsSource and StemsVocalSource (two new
    // constants) and PluginTrackStem.Engine (an init-only property, so the constructor a
    // plugin was compiled against is still there).
    // 12.11 added PluginLibrary.EncodingPresets and PluginEncodingPreset: the presets that
    // encode a library's folders, each with the tallest video it can produce. Additive: an
    // init-only property defaulting to empty, so new PluginLibrary(id, title, type) written
    // before it still constructs and still reads Id, Title and Type.
    // 12.11 also added PluginDocs (constants: the docs page each refusal links) and
    // PluginLibrary's own Equals and GetHashCode, which compare the presets by value so two
    // reads of one library stay equal.
    // 12.11 also added NmPluginForbiddenComponents (NoMercy.Design), the refusal code
    // PluginRefusalCodes.ViewComponentForbidden and PluginRefusalMessages.ViewComponentForbidden:
    // the server refuses a view that names NMServerComponent at any depth. New members only.
    // 12.11 also added the shelf builders and the paging contract: PluginShelves, PluginShelf,
    // PluginShelfCard, PluginPaging, PluginPageRequest, PluginClientFeatures,
    // PluginActionType.LoadPage, PluginActionIntent.LoadPage, PluginViewRequest.Page and
    // PluginViewRequest.Features (read from Query, never sent over the wire) and
    // PluginDocs.ShelvesAndPaging. Additive: new types, a new constant and new read-only
    // members; PluginViews and every node shape are unchanged.
    // 12.11 also added PluginRefusalCodes.OutOfProcessUnsupported: a plugin set to run in its
    // own process that uses something that process does not carry is refused, not started.
    // 12.12 added PluginClientFeatures.Rows ("rows", a new constant): the client
    // draws a plugin's NMCarousel as its own home-page row and draws NMLolomo.
    // 12.13 added PluginLibraryShow.AddedBy (an init-only property, so the constructor a
    // plugin was compiled against is still there) and PluginLibraryAddedBy (two constants):
    // why a show is in its library, manual or file, null when the server does not say.
    public static Version Current { get; } = new(12, 13);

    /// <summary>
    /// The oldest major this server can load at all.
    /// <para>
    /// Usually one behind <see cref="Current" />: a major removes members, and
    /// a plugin that never called them keeps working, so the grace costs
    /// nothing and saves every author a forced rebuild.
    /// </para>
    /// <para>
    /// Twelve is not that kind of major. The SDK assembly and every namespace
    /// in it were renamed, so a plugin built against eleven resolves no type at
    /// all. Granting it grace would install a plugin that then fails at load
    /// with a missing-type error naming nothing its author recognises, instead
    /// of a refusal that says rebuild.
    /// </para>
    /// </summary>
    public static Version Oldest { get; } = new(12, 0);

    public static bool IsCompatible(string? targetAbi)
    {
        if (string.IsNullOrWhiteSpace(targetAbi))
        {
            return true;
        }

        if (!Version.TryParse(targetAbi, out Version? requested))
        {
            return false;
        }

        if (requested.Major < Oldest.Major)
        {
            return false;
        }

        if (requested.Major < Current.Major)
        {
            return true;
        }

        return requested.Major == Current.Major && requested.Minor <= Current.Minor;
    }
}
