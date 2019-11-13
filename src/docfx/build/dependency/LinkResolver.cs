// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.DocAsCode.MarkdigEngine.Extensions;

namespace Microsoft.Docs.Build
{
    internal class LinkResolver
    {
        private readonly Input _input;
        private readonly Docset _docset;
        private readonly Docset _fallbackDocset;
        private readonly BuildScope _buildScope;
        private readonly RedirectionProvider _redirectionProvider;
        private readonly WorkQueue<Document> _buildQueue;
        private readonly DocumentProvider _documentProvider;
        private readonly BookmarkValidator _bookmarkValidator;
        private readonly DependencyMapBuilder _dependencyMapBuilder;
        private readonly GitCommitProvider _gitCommitProvider;
        private readonly XrefResolver _xrefResolver;
        private readonly TemplateEngine _templateEngine;
        private readonly FileLinkMapBuilder _fileLinkMapBuilder;

        public LinkResolver(
            Docset docset,
            Docset fallbackDocset,
            Input input,
            BuildScope buildScope,
            WorkQueue<Document> buildQueue,
            RedirectionProvider redirectionProvider,
            DocumentProvider documentProvider,
            GitCommitProvider gitCommitProvider,
            BookmarkValidator bookmarkValidator,
            DependencyMapBuilder dependencyMapBuilder,
            XrefResolver xrefResolver,
            TemplateEngine templateEngine,
            FileLinkMapBuilder fileLinkMapBuilder)
        {
            _input = input;
            _docset = docset;
            _fallbackDocset = fallbackDocset;
            _buildScope = buildScope;
            _buildQueue = buildQueue;
            _redirectionProvider = redirectionProvider;
            _documentProvider = documentProvider;
            _bookmarkValidator = bookmarkValidator;
            _dependencyMapBuilder = dependencyMapBuilder;
            _gitCommitProvider = gitCommitProvider;
            _xrefResolver = xrefResolver;
            _templateEngine = templateEngine;
            _fileLinkMapBuilder = fileLinkMapBuilder;
        }

        public (Error error, string content, Document file) ResolveContent(
            SourceInfo<string> path, Document referencingFile, DependencyType dependencyType = DependencyType.Inclusion)
        {
            var (error, content, child) = TryResolveContent(referencingFile, path);

            _dependencyMapBuilder.AddDependencyItem(referencingFile, child, dependencyType);

            return (error, content, child);
        }

        public (Error error, string link, Document file) ResolveRelativeLink(
            Document relativeToFile, SourceInfo<string> path, Document referencingFile)
        {
            var (error, link, file) = ResolveAbsoluteLink(path, referencingFile, relativeToFile);

            if (file != null)
            {
                link = UrlUtility.GetRelativeUrl(relativeToFile.SiteUrl, link);
            }

            return (error, link, file);
        }

        public (Error error, string link, Document file) ResolveAbsoluteLink(
            SourceInfo<string> path, Document referencingFile, Document relativeToFile)
        {
            var (error, link, fragment, linkType, file, isCrossReference) = TryResolveAbsoluteLink(referencingFile, path, relativeToFile);

            if (file != null)
            {
                _buildQueue.Enqueue(file);
            }

            // NOTE: bookmark validation result depend on current inclusion stack
            relativeToFile = relativeToFile ?? referencingFile;
            var isSelfBookmark = linkType == LinkType.SelfBookmark || relativeToFile == file;
            if (!isCrossReference && (isSelfBookmark || file != null))
            {
                _dependencyMapBuilder.AddDependencyItem(referencingFile, file, UrlUtility.FragmentToDependencyType(fragment));
                _bookmarkValidator.AddBookmarkReference(
                    referencingFile, isSelfBookmark ? relativeToFile : file, fragment, isSelfBookmark, path);
            }

            _fileLinkMapBuilder.AddFileLink(relativeToFile, link);

            return (error, link, file);
        }

        private (Error error, string content, Document file) TryResolveContent(Document referencingFile, SourceInfo<string> href)
        {
            var (error, file, _, _, _) = TryResolveFile(referencingFile, href, inclusion: true);

            if (file?.ContentType == ContentType.Redirection)
            {
                return default;
            }

            return file != null ? (error, _input.ReadString(file.FilePath), file) : default;
        }

        private (Error error, string href, string fragment, LinkType linkType, Document file, bool isCrossReference) TryResolveAbsoluteLink(
            Document referencingFile, SourceInfo<string> href, Document relativeToFile)
        {
            Debug.Assert(href != null);

            if (href.Value.StartsWith("xref:"))
            {
                var uid = new SourceInfo<string>(href.Value.Substring("xref:".Length), href);
                var (uidError, uidHref, _, declaringFile) = _xrefResolver.ResolveAbsoluteXref(uid, referencingFile, relativeToFile);
                var xrefLinkType = declaringFile != null ? LinkType.RelativePath : LinkType.External;

                return (uidError, uidHref, null, xrefLinkType, declaringFile, true);
            }

            var decodedHref = new SourceInfo<string>(Uri.UnescapeDataString(href), href);
            var (error, file, query, fragment, linkType) = TryResolveFile(referencingFile, decodedHref);

            if (linkType == LinkType.WindowsAbsolutePath)
            {
                return (error, "", fragment, linkType, null, false);
            }

            // Cannot resolve the file, leave href as is
            if (file is null)
            {
                return (error, href, fragment, linkType, null, false);
            }

            // Self reference, don't build the file, leave href as is
            if (file == referencingFile)
            {
                var selfUrl = linkType == LinkType.SelfBookmark ? "" : Path.GetFileName(file.SiteUrl);

                return (error, UrlUtility.MergeUrl(selfUrl, query, fragment), fragment, LinkType.SelfBookmark, null, false);
            }

            if (file?.FilePath.Origin == FileOrigin.Redirection)
            {
                return (error, UrlUtility.MergeUrl(file.SiteUrl, query, fragment), null, linkType, file, false);
            }

            if (error is null && _buildScope.OutOfScope(file))
            {
                return (Errors.LinkOutOfScope(href, file), href, fragment, linkType, null, false);
            }

            return (error, UrlUtility.MergeUrl(file.SiteUrl, query, fragment), fragment, linkType, file, false);
        }

        private (Error error, Document file, string query, string fragment, LinkType linkType) TryResolveFile(
            Document referencingFile, SourceInfo<string> href, bool inclusion = false)
        {
            href = href.Or("");
            var (path, query, fragment) = UrlUtility.SplitUrl(href);

            switch (UrlUtility.GetLinkType(href))
            {
                case LinkType.SelfBookmark:
                    return (null, referencingFile, query, fragment, LinkType.SelfBookmark);

                case LinkType.WindowsAbsolutePath:
                    return (Errors.LocalFilePath(href), null, null, null, LinkType.WindowsAbsolutePath);

                case LinkType.RelativePath:
                    if (string.IsNullOrEmpty(path))
                    {
                        // https://tools.ietf.org/html/rfc2396#section-4.2
                        // a hack way to process empty href
                        return (null, referencingFile, query, fragment, LinkType.SelfBookmark);
                    }

                    // resolve file
                    var lookupFallbackCommits = inclusion || _documentProvider.GetContentType(path) == ContentType.Resource;
                    var file = TryResolveRelativePath(referencingFile.FilePath, path, lookupFallbackCommits);

                    // for LandingPage should not be used,
                    // it is a hack to handle some specific logic for landing page based on the user input for now
                    // which needs to be removed once the user input is correct
                    if (_templateEngine != null && TemplateEngine.IsLandingData(referencingFile.Mime))
                    {
                        if (file is null)
                        {
                            // try to resolve with .md for landing page
                            file = TryResolveRelativePath(referencingFile.FilePath, $"{path}.md", lookupFallbackCommits);
                        }

                        // Do not report error for landing page
                        return (null, file, query, fragment, LinkType.RelativePath);
                    }

                    if (file is null)
                    {
                        return (Errors.FileNotFound(
                            new SourceInfo<string>(path, href)), null, query, fragment, LinkType.RelativePath);
                    }

                    return (null, file, query, fragment, LinkType.RelativePath);

                default:
                    return default;
            }
        }

        private Document TryResolveRelativePath(FilePath referencingFile, string relativePath, bool lookupFallbackCommits)
        {
            FilePath path;
            PathString pathToDocset;

            if (relativePath.StartsWith("~/") || relativePath.StartsWith("~\\"))
            {
                // Treat ~/ as path relative to docset
                pathToDocset = new PathString(relativePath.Substring(2).TrimStart('/', '\\'));
            }
            else
            {
                // Path relative to referencing file
                var baseDirectory = Path.GetDirectoryName(referencingFile.GetPathToOrigin());
                pathToDocset = new PathString(Path.Combine(baseDirectory, relativePath));
            }

            // use the actual file name case
            if (_buildScope.GetActualFileName(pathToDocset, out var pathActualCase))
            {
                pathToDocset = pathActualCase;
            }

            // resolve from the current docset for files in dependencies
            if (referencingFile.Origin == FileOrigin.Dependency)
            {
                path = new FilePath(pathToDocset, referencingFile.DependencyName);
                if (_input.Exists(path))
                {
                    return _documentProvider.GetDocument(path);
                }
                return null;
            }

            // resolve from redirection files
            path = new FilePath(pathToDocset, FileOrigin.Redirection);
            if (_redirectionProvider.Contains(path))
            {
                return _documentProvider.GetDocument(path);
            }

            // resolve from dependent docsets
            foreach (var (dependencyName, _) in _docset.Config.Dependencies)
            {
                var (match, _, remainingPath) = PathUtility.Match(pathToDocset, dependencyName);
                if (!match)
                {
                    // the file stored in the dependent docset should start with dependency name
                    continue;
                }

                path = new FilePath(remainingPath, dependencyName);
                if (_input.Exists(path))
                {
                    return _documentProvider.GetDocument(path);
                }
            }

            // resolve from entry docset
            path = new FilePath(pathToDocset);
            if (_input.Exists(path))
            {
                return _documentProvider.GetDocument(path);
            }

            // resolve from fallback docset
            if (_fallbackDocset != null)
            {
                path = new FilePath(pathToDocset, FileOrigin.Fallback);
                if (_input.Exists(path))
                {
                    return _documentProvider.GetDocument(path);
                }

                // resolve from fallback docset git commit history
                if (lookupFallbackCommits)
                {
                    var (repo, _, commits) = _gitCommitProvider.GetCommitHistory(_fallbackDocset, pathToDocset);
                    var commit = repo != null && commits.Count > 1 ? commits[1] : default;
                    path = new FilePath(pathToDocset, commit?.Sha, FileOrigin.Fallback);
                    if (_input.Exists(path))
                    {
                        return _documentProvider.GetDocument(path);
                    }
                }
            }

            return default;
        }
    }
}
