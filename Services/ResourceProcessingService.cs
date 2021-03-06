﻿using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ExCSS;
using ExCSS.Model;
using Orchard.Environment.Extensions;
using Piedone.Combinator.EventHandlers;
using Piedone.Combinator.Extensions;
using Piedone.Combinator.Models;
using Piedone.Combinator.SpriteGenerator;
using Piedone.HelpfulLibraries.Utilities;

namespace Piedone.Combinator.Services
{
    [OrchardFeature("Piedone.Combinator")]
    public class ResourceProcessingService : IResourceProcessingService
    {
        private readonly IResourceFileService _resourceFileService;
        private readonly IMinificationService _minificationService;
        private readonly ICacheFileService _cacheFileService;
        private readonly ICombinatorResourceEventHandler _eventHandler;
        private delegate string ImageMatchProcessor(string url, string extension, Match match);


        public ResourceProcessingService(
            IResourceFileService resourceFileService,
            IMinificationService minificationService,
            ICacheFileService cacheFileService,
            ICombinatorResourceEventHandler eventHandler)
        {
            _resourceFileService = resourceFileService;
            _minificationService = minificationService;
            _cacheFileService = cacheFileService;
            _eventHandler = eventHandler;
        }


        public void ProcessResource(CombinatorResource resource, StringBuilder combinedContent, ICombinatorSettings settings)
        {
            if (resource.IsCdnResource && !settings.CombineCdnResources)
            {
                resource.IsOriginal = true;
                return;
            }


            var absoluteUrlString = resource.AbsoluteUrl.ToString();

            _resourceFileService.LoadResourceContent(resource);

            _eventHandler.OnContentLoaded(resource);

            if (string.IsNullOrEmpty(resource.Content)) return;

            if (resource.Type == ResourceType.Style)
            {
                //var stylesheet = new StylesheetParser().Parse(resource.Content);
                //AdjustRelativePaths(resource, stylesheet);

                //if (settings.EmbedCssImages && (settings.EmbedCssImagesStylesheetExcludeFilter == null || !settings.EmbedCssImagesStylesheetExcludeFilter.IsMatch(absoluteUrlString)))
                //{
                //    EmbedImages(resource, stylesheet, settings.EmbeddedImagesMaxSizeKB);
                //}

                //resource.Content = stylesheet.ToString();

                // Needed until ExCss becomes mature
                RegexAdjustRelativePaths(resource);

                if (settings.EmbedCssImages && (settings.EmbedCssImagesStylesheetExcludeFilter == null || !settings.EmbedCssImagesStylesheetExcludeFilter.IsMatch(absoluteUrlString)))
                {
                    RegexEmbedImages(resource, settings.EmbeddedImagesMaxSizeKB);
                }
            }

            if (settings.MinifyResources && (settings.MinificationExcludeFilter == null || !settings.MinificationExcludeFilter.IsMatch(absoluteUrlString)))
            {
                MinifyResourceContent(resource);
                if (string.IsNullOrEmpty(resource.Content)) return;
            }

            _eventHandler.OnContentProcessed(resource);

            combinedContent.Append(resource.Content);
        }

        public void ReplaceCssImagesWithSprite(CombinatorResource resource)
        {
            Func<RuleSet, Term, bool> noSprite =
                (ruleSet, url) =>
                {
                    if (url.Value.Contains("no-sprite")
                        || ruleSet.Selectors.Any(selector => selector.SimpleSelectors.Any(simpleSelector => simpleSelector.Class == "no-sprite"))) return true;

                    // Images with a background position are not suitable for sprite generation if they aren't top-left
                    if (ruleSet.Declarations.Any(declaration => declaration.Name == "background-position"
                        && (declaration.Expression.Terms.Count() != 2
                                || !declaration.Expression.Terms.Any(term => term.Value == "top")
                                || !declaration.Expression.Terms.Any(term => term.Value == "left")))) return true;

                    // Images with a background size are not suitable for sprite generation
                    if (ruleSet.Declarations.Any(declaration => declaration.Name == "background-size")) return true;

                    // Images with a background repeat are not suitable for sprite generation
                    if (ruleSet.Declarations.Any(declaration => declaration.Name == "background-repeat"
                        && (declaration.Expression.Terms.Count() != 1 || declaration.Expression.Terms.First().Value != "no-repeat"))) return true;

                    var backgroundTerms =
                        ruleSet.Declarations
                            .Where(declaration => declaration.Name == "background")
                            .SelectMany(declaration => declaration.Expression.Terms);

                    // Sized backgrounds are not suitable either
                    // LineHeightTerm is filled with a value when if in the shorthand background declaration there's the background size specified, e.g.:
                    // background: url(url-to-img) 300px 400px / 500px 600px no-repeat;
                    // Now there will be a term for 400px, having LineHeightTerm specified for 500px.
                    if (backgroundTerms.Any(term => term.LineHeightTerm != null)) return true;

                    var backgroundTermValues = backgroundTerms.Select(term => term.Value);

                    // Positioned backgrounds are not suitable either, except top-left ones
                    if (backgroundTerms
                        .Any(term =>
                            term.Value == "center" ||
                            term.Value == "top" ||
                            term.Value == "right" ||
                            term.Value == "bottom" ||
                            term.Unit != null) &&
                            !(backgroundTermValues.Contains("top") && backgroundTermValues.Contains("left"))) return true;

                    if (backgroundTermValues.Any(value => value == "repeat-x" || value == "repeat-y" || value == "repeat")) return true;

                    return false;
                };

            var images = new Dictionary<string, CssImage>();
            var stylesheet = new StylesheetParser().Parse(resource.Content);

            ProcessImageUrls(
                resource,
                stylesheet,
                (ruleSet, urlTerm) =>
                {
                    var url = urlTerm.Value;

                    if (noSprite(ruleSet, urlTerm)) return;

                    var imageContent = _resourceFileService.GetImageContent(InlineUriFactory(resource, url));

                    if (imageContent.Length / 1024 <= 5000)
                    {
                        images[url] = new CssImage { Content = imageContent };
                    }
                });

            if (images.Count == 0) return;

            _cacheFileService.WriteSpriteStream(
                resource.Content.GetHashCode() + ".jpg",
                (stream, publicUrl) =>
                {
                    using (var sprite = new Sprite(images.Values.Select(image => image.Content)))
                    {
                        var imageEnumerator = images.Values.GetEnumerator();
                        foreach (var backgroundImage in sprite.Generate(stream, ImageFormat.Jpeg))
                        {
                            imageEnumerator.MoveNext();
                            imageEnumerator.Current.BackgroundImage = backgroundImage;
                            imageEnumerator.Current.BackgroundImage.Url = InlineUriFactory(resource, publicUrl);
                        }
                    }
                });

            ProcessImageUrls(
                resource,
                stylesheet,
                (ruleSet, urlTerm) =>
                {
                    var url = urlTerm.Value;

                    if (images.ContainsKey(url))
                    {
                        var backgroundImage = images[url].BackgroundImage;

                        var imageDeclaration = new Declaration
                        {
                            Name = "background-image",
                            Expression = new Expression
                            {
                                Terms = new List<Term>
                                        {
                                            new Term { Type = TermType.Url, Value = backgroundImage.Url.ToStringWithoutScheme() }
                                        }
                            }
                        };
                        ruleSet.Declarations.Add(imageDeclaration);

                        var bgPosition = backgroundImage.Position;
                        var positionDeclaration = new Declaration
                        {
                            Name = "background-position",
                            Expression = new Expression
                            {
                                Terms = new List<Term>
                                        {
                                            new Term { Type = TermType.Number, Value = bgPosition.X.ToString(), Unit = Unit.Px },
                                            new Term { Type = TermType.Number, Value = bgPosition.Y.ToString(), Unit = Unit.Px }
                                        }
                            }
                        };
                        ruleSet.Declarations.Add(positionDeclaration);
                    }
                });

            resource.Content = stylesheet.ToString();
        }


        public static void ConvertRelativeUrlsToAbsolute(CombinatorResource resource, Uri baseUri)
        {
            if (string.IsNullOrEmpty(resource.Content) || resource.Type != ResourceType.Style) return;

            var stylesheet = new StylesheetParser().Parse(resource.Content);

            // Modifying relative urls (because when saved, local urls were converted to unified relative ones) to point to the original domain
            ProcessUrls(
                resource,
                stylesheet,
                (ruleSet, urlTerm) =>
                {
                    if (urlTerm.Value.StartsWith("//") || Uri.IsWellFormedUriString(urlTerm.Value, UriKind.Absolute)) return;
                    
                    urlTerm.Value = UriHelper.Combine(baseUri.ToStringWithoutScheme(), urlTerm.Value);
                });
        }


        private void EmbedImages(CombinatorResource resource, Stylesheet stylesheet, int maxSizeKB)
        {
            ProcessImageUrls(
                resource,
                stylesheet,
                (ruleSet, urlTerm) =>
                {
                    var url = urlTerm.Value;
                    var imageContent = _resourceFileService.GetImageContent(InlineUriFactory(resource, url));

                    if (imageContent.Length / 1024 <= maxSizeKB)
                    {
                        var dataUrl =
                        "data:image/"
                            + Path.GetExtension(url).Replace(".", "")
                            + ";base64,"
                            + Convert.ToBase64String(imageContent);

                        urlTerm.Value = dataUrl;
                    }
                });
        }


        // This will be needed until ExCSS becomes mature
        #region Legacy Regex-based CSS processing
        public static void RegexConvertRelativeUrlsToAbsolute(CombinatorResource resource, Uri baseUri)
        {
            if (string.IsNullOrEmpty(resource.Content) || resource.Type != ResourceType.Style) return;

            RegexProcessUrls(resource,
                (match) =>
                {
                    var url = match.Groups[1].ToString();

                    if (!url.StartsWith("//") && !Uri.IsWellFormedUriString(url, UriKind.Absolute))
                    {
                        url = UriHelper.Combine(baseUri.ToStringWithoutScheme(), url);
                    }

                    return "url(\"" + url + "\")";
                });
        }

        private void RegexProcessImageUrls(CombinatorResource resource, ImageMatchProcessor matchProcessor)
        {
            RegexProcessUrls(resource,
                (match) =>
                {
                    var url = match.Groups[1].Value;
                    var extension = Path.GetExtension(url).ToLowerInvariant();

                    // This is a dumb check but otherwise we'd have to inspect the file thoroughly
                    if (!string.IsNullOrEmpty(extension) && ".jpg .jpeg .png .gif .tiff .bmp".Contains(extension))
                    {
                        var result = matchProcessor(url, extension, match);
                        if (result != null) return result;
                    }

                    return match.Groups[0].Value;
                });
        }

        private void RegexEmbedImages(CombinatorResource resource, int maxSizeKB)
        {
            RegexProcessImageUrls(resource,
                (url, extenstion, match) =>
                {
                    var imageContent = _resourceFileService.GetImageContent(RegexMakeInlineUri(resource, url));

                    if (imageContent.Length / 1024 <= maxSizeKB)
                    {
                        var dataUrl =
                        "data:image/"
                            + Path.GetExtension(url).Replace(".", "")
                            + ";base64,"
                            + Convert.ToBase64String(imageContent);

                        return "url(\"" + dataUrl + "\")";
                    }

                    return null;
                });
        }

        private static void RegexAdjustRelativePaths(CombinatorResource resource)
        {
            RegexProcessUrls(resource,
                (match) =>
                {
                    var url = match.Groups[1].ToString();

                    var uri = RegexMakeInlineUri(resource, url);

                    // Remote paths are preserved as full urls, local paths become uniformed relative ones.
                    string uriString = "";
                    if (uri.Scheme != "data")
                    {
                        if (resource.IsCdnResource || resource.AbsoluteUrl.Host != uri.Host) uriString = uri.ToStringWithoutScheme();
                        else uriString = uri.PathAndQuery;
                    }
                    else
                    {
                        uriString = url;
                    }

                    return "url(\"" + uriString + "\")";
                });
        }

        private static void RegexProcessUrls(CombinatorResource resource, MatchEvaluator evaluator)
        {
            string content = resource.Content;

            content = Regex.Replace(
                                    content,
                                    "url\\(['|\"]?(.+?)['|\"]?\\)",
                                    evaluator,
                                    RegexOptions.IgnoreCase);

            resource.Content = content;
        }

        private static Uri RegexMakeInlineUri(CombinatorResource resource, string url)
        {
            return Uri.IsWellFormedUriString(url, UriKind.Absolute) ? new Uri(url) : new Uri(resource.AbsoluteUrl, url);
        }
        #endregion


        private void MinifyResourceContent(CombinatorResource resource)
        {
            if (resource.Type == ResourceType.Style)
            {
                resource.Content = _minificationService.MinifyCss(resource.Content);
            }
            else if (resource.Type == ResourceType.JavaScript)
            {
                resource.Content = _minificationService.MinifyJavaScript(resource.Content);
            }
        }


        private static void AdjustRelativePaths(CombinatorResource resource, Stylesheet stylesheet)
        {
            ProcessUrls(
                resource,
                stylesheet,
                (ruleSet, urlTerm) =>
                {
                    var uri = InlineUriFactory(resource, urlTerm.Value);

                    // Remote paths are preserved as full urls, local paths become uniformed relative ones.
                    if (uri.Scheme != "data")
                    {
                        string uriString = "";

                        if (resource.IsCdnResource || resource.AbsoluteUrl.Host != uri.Host) uriString = uri.ToStringWithoutScheme();
                        else uriString = uri.PathAndQuery;

                        urlTerm.Value = uriString;
                    }
                });
        }

        private static void ProcessUrls(CombinatorResource resource, Stylesheet stylesheet, Action<RuleSet, Term> processor)
        {
            var items =
                stylesheet.RuleSets
                    .SelectMany(ruleset => ruleset.Declarations
                        .SelectMany(declaration => declaration.Expression.Terms
                            .Where(term => term.Type == TermType.Url)
                            .Select(term => new { RuleSet = ruleset, Term = term })))
                .ToList();

            // Projected to a list so for can be used. This makes it possible to modify the collection from processors.
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                processor(item.RuleSet, item.Term);
            }
        }

        private static void ProcessImageUrls(CombinatorResource resource, Stylesheet stylesheet, Action<RuleSet, Term> processor)
        {
            ProcessUrls(
                resource,
                stylesheet,
                (ruleSet, urlTerm) =>
                {
                    var url = urlTerm.Value;
                    var extension = Path.GetExtension(url).ToLowerInvariant();

                    // This is a dumb check but otherwise we'd have to inspect the file thoroughly
                    if (!string.IsNullOrEmpty(extension) && ".jpg .jpeg .png .gif .tiff .bmp".Contains(extension))
                    {
                        processor(ruleSet, urlTerm);
                    }
                });
        }

        private static Uri InlineUriFactory(CombinatorResource resource, string url)
        {
            return Uri.IsWellFormedUriString(url, UriKind.Absolute) ? new Uri(url) : new Uri(resource.AbsoluteUrl, url);
        }


        private class CssImage
        {
            public byte[] Content { get; set; }
            public BackgroundImage BackgroundImage { get; set; }
        }
    }
}