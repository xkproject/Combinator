﻿using System;
using System.Collections.Generic;
using System.Web;
using Orchard.Mvc;
using Orchard.UI.Resources;
using Piedone.Combinator.Extensions;

namespace Piedone.Combinator.Models
{
    public class CombinatorResource
    {
        private readonly HttpContextBase _httpContext;

        #region Path handling
        private string NormalizedFullPath
        {
            get
            {
                var fullPath = RequiredContext.Resource.GetFullPath();
                if (string.IsNullOrEmpty(fullPath)) return "";

                if (fullPath.StartsWith("//"))
                {
                    return _httpContext.Request.Url.Scheme + ":" + fullPath; // For urls with the "//domain.com" notation
                }

                return fullPath;
            }
        }

        private string ApplicationPath
        {
            get { return _httpContext.Request.ApplicationPath; }
        }


        public Uri AbsoluteUrl
        {
            get
            {
                var fullPath = NormalizedFullPath;

                if (Uri.IsWellFormedUriString(fullPath, UriKind.Absolute)) return new Uri(fullPath);
                return new Uri(_httpContext.Request.Url, RelativeUrl);
            }
        }

        public Uri RelativeUrl
        {
            get
            {
                // This should be the same as the NormalizedFullPath if that is a local relative url, but this is safer.
                return new Uri(VirtualPathUtility.ToAbsolute(RelativeVirtualPath, ApplicationPath), UriKind.Relative);
            }
        }

        public string RelativeVirtualPath
        {
            get
            {
                if (string.IsNullOrEmpty(NormalizedFullPath)) return "~/";
                return VirtualPathUtility.ToAppRelative(NormalizedFullPath, ApplicationPath);
            }
        }
        #endregion

        private ResourceRequiredContext _requiredContext;
        public ResourceRequiredContext RequiredContext
        {
            get { return _requiredContext; }
            set
            {
                _requiredContext = value;

                if (_requiredContext.Resource != null)
                {
                    if (IsCdnResource)
                    {
                        _requiredContext.Resource.SetUrlProtocolRelative(AbsoluteUrl);
                    }
                    else
                    {
                        _requiredContext.Resource.SetUrl(RelativeUrl.ToString());
                    } 
                }
            }
        }

        public bool IsCdnResource
        {
            get
            {
                var fullPath = NormalizedFullPath;

                return 
                    !IsRemoteStorageResource &&
                    Uri.IsWellFormedUriString(fullPath, UriKind.Absolute)
                    && new Uri(fullPath).Authority != _httpContext.Request.Url.Authority;
            }
        }

        /// <summary>
        /// <c>true</c> if the resource comes from a non-local storage like Azure Blob Storage. This shows a valid value only after the resource
        /// was saved (as before that there is no way to tell if it will be stored in a remote storage or not).
        /// </summary>
        public bool IsRemoteStorageResource { get; set; }

        public bool IsConditional
        {
            get { return !string.IsNullOrEmpty(RequiredContext.Settings.Condition); }
        }

        private readonly ResourceType _type;
        public ResourceType Type { get { return _type; } }

        public DateTime LastUpdatedUtc { get; set; }

        public string Content { get; set; }

        /// <summary>
        /// Indicates that the resource was not touched and was kept in its original state.
        /// </summary>
        public bool IsOriginal { get; set; }


        public CombinatorResource(ResourceType type, IHttpContextAccessor httpContextAccessor)
        {
            _type = type;
            _httpContext = httpContextAccessor.Current();

            RequiredContext = new ResourceRequiredContext();
            IsOriginal = false;
        }


        public void FillRequiredContext(string name, string url, string culture = "", string condition = "", Dictionary<string, string> attributes = null, IDictionary<string, string> tagAttributes = null)
        {
            var requiredContext = new ResourceRequiredContext();
            var resourceManifest = new ResourceManifest();
            requiredContext.Resource = resourceManifest.DefineResource(Type.ToStringType(), name);
            if (!string.IsNullOrEmpty(url)) requiredContext.Resource.SetUrl(url);
            requiredContext.Settings = new RequireSettings
            {
                Name = name,
                Culture = culture,
                Condition = condition,
                Attributes = attributes != null ? new Dictionary<string, string>(attributes) : new Dictionary<string, string>()
            };
            RequiredContext = requiredContext;

            if (tagAttributes != null) requiredContext.Resource.TagBuilder.MergeAttributes(tagAttributes);
        }

        public bool SettingsEqual(CombinatorResource other)
        {
            // If one's RequiredContext is null, their settings are not identical...
            if (RequiredContext == null ^ other.RequiredContext == null) return false;

            // However if both of them are null, we say the settings are identical
            if (RequiredContext == null && other.RequiredContext == null) return true;

            var settings = RequiredContext.Settings;
            var otherSettings = other.RequiredContext.Settings;
            return
                settings.Culture == otherSettings.Culture
                && settings.Condition == otherSettings.Condition
                && settings.AttributesEqual(otherSettings)
                && RequiredContext.Resource.TagAttributesEqual(other.RequiredContext.Resource);
        }
    }
}