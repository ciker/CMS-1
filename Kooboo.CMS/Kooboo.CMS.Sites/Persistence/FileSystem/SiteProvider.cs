﻿#region License
// 
// Copyright (c) 2013, Kooboo team
// 
// Licensed under the BSD License
// See the file LICENSE.txt for details.
// 
#endregion
using Ionic.Zip;
using Kooboo.CMS.Common;
using Kooboo.CMS.Common.Persistence.Non_Relational;
using Kooboo.CMS.Membership.Models;
using Kooboo.CMS.Membership.Persistence;
using Kooboo.CMS.Sites.Globalization;
using Kooboo.CMS.Sites.Models;
using Kooboo.Globalization;
using Kooboo.IO;
using Kooboo.Web.Url;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Kooboo.CMS.Sites.Persistence.FileSystem
{
    [Kooboo.CMS.Common.Runtime.Dependency.Dependency(typeof(ISiteProvider))]
    [Kooboo.CMS.Common.Runtime.Dependency.Dependency(typeof(IProvider<Site>))]
    public class SiteProvider : ISiteProvider
    {
        #region Static
        static string Offline_File = "offline.txt";
        static string ContentDatabaseFileName = "ContentDatabase";
        static string MembershipFileName = "Membership";
        static System.Threading.ReaderWriterLockSlim locker = new System.Threading.ReaderWriterLockSlim();
        #endregion

        #region .ctor
        IBaseDir baseDir;
        IMembershipProvider _membershipProvider;
        ILabelProvider _labelProvider;
        public SiteProvider(IBaseDir baseDir, IMembershipProvider membershipProvider, ILabelProvider labelProvider)
        {
            this.baseDir = baseDir;
            this._membershipProvider = membershipProvider;
            this._labelProvider = labelProvider;
        }
        #endregion

        #region IProvider<Site> Members

        [Obsolete("The method do not supported in SiteProvider.")]
        public IQueryable<Models.Site> All(Site site)
        {
            throw new NotSupportedException("The method do not supported in SiteProvider.");
        }

        public virtual void Add(Models.Site item)
        {
            Save(item);
        }

        private static void Save(Models.Site item)
        {
            locker.EnterWriteLock();
            try
            {
                IOUtility.EnsureDirectoryExists(item.PhysicalPath);
                Serialization.Serialize(item, item.DataFile);
                ((IPersistable)item).OnSaved();
            }
            finally
            {
                locker.ExitWriteLock();
            }

        }

        public virtual void Update(Models.Site @new, Models.Site old)
        {
            Save(@new);
        }

        public virtual void Remove(Models.Site item)
        {
            locker.EnterWriteLock();
            try
            {
                IOUtility.DeleteDirectory(item.PhysicalPath, true);
                ClearSiteData(item);
            }
            finally
            {
                locker.ExitWriteLock();
            }
        }

        private void ClearSiteData(Site site)
        {
            //try
            //{
            //    _elementRepositoryFactory.CreateRepository(site).Clear();
            //}
            //catch
            //{
            //}
            try
            {
                Kooboo.CMS.Sites.Persistence.Providers.PageProvider.Clear(site);
            }
            catch
            {
            }
            try
            {
                Kooboo.CMS.Sites.Persistence.Providers.HtmlBlockProvider.Clear(site);
            }
            catch
            {
            }
        }

        public virtual Site Get(Site dummyObject)
        {
            if (!dummyObject.Exists())
            {
                return null;
            }
            locker.EnterReadLock();
            try
            {
                var site = Serialization.DeserializeSettings<Site>(dummyObject.DataFile);
                ((IPersistable)site).Init(dummyObject);
                return site;
            }
            finally
            {
                locker.ExitReadLock();
            }
        }

        #endregion

        #region Query Members
        private class DomainMappingComparer : IComparer<DomainMapping>
        {
            public int Compare(DomainMapping x, DomainMapping y)
            {
                if (x.FullDomain.EqualsOrNullEmpty(y.FullDomain, StringComparison.InvariantCultureIgnoreCase) && x.UserAgent.EqualsOrNullEmpty(y.UserAgent, StringComparison.InvariantCultureIgnoreCase))
                {
                    return 0;
                }
                //取反，从长到短排序
                var result = -(x.FullDomain.Length.CompareTo(y.FullDomain.Length));
                if (result == 0)
                {
                    result = x.FullDomain.CompareTo(y.FullDomain);
                    if (result == 0)
                    {
                        //有useragent的优先匹配
                        result = -(x.UserAgent.CompareTo(y.UserAgent));
                    }
                }
                return result;
            }
        }
        public virtual IEnumerable<DomainMapping> GetDomainTable()
        {
            SortedSet<DomainMapping> domainList = new SortedSet<DomainMapping>(new DomainMappingComparer());
            //SortedDictionary<string, Site> table = new SortedDictionary<string, Site>(new StringLengthComparer());
            foreach (var site in AllSites())
            {
                var siteObject = site.AsActual();
                foreach (var domain in siteObject.FullDomains)
                {
                    domainList.Add(new DomainMapping(domain, siteObject.UserAgent, siteObject));
                }
            }

            return domainList;// new SortedDictionary<string, Site>(table, new StringLengthComparer());

        }
        public virtual Site GetSiteByHostNameNPath(string hostName, string requestPath)
        {
            var domainTable = GetDomainTable();
            var fullPath = hostName + "/" + requestPath.Trim('/') + "/";
            return domainTable.Where(it => fullPath.StartsWith(it.FullDomain, StringComparison.OrdinalIgnoreCase))
                .Select(it => it.SiteObject).FirstOrDefault();
        }

        //private Site GetSiteByPredicate(Func<Site, bool> predicate)
        //{
        //    foreach (var site in Providers.SiteProvider.AllSites())
        //    {
        //        if (site.Exists())
        //        {
        //            var detail = site.AsActual();
        //            if (predicate(detail))
        //            {
        //                return detail;
        //            }
        //        }
        //    }
        //    return null;
        //}

        //public Site GetSiteByHostName(string hostName)
        //{
        //    return GetSiteByPredicate(it => (it.Domains != null && it.Domains.Length > 0) &&
        //        it.Domains.Contains(hostName, StringComparer.OrdinalIgnoreCase) && string.IsNullOrEmpty(it.SitePath));
        //}

        IEnumerable<Site> ISiteElementProvider<Site>.All(Site site)
        {
            throw new NotImplementedException();
        }

        public virtual IEnumerable<Site> All()
        {
            return AllRootSites();
        }
        public virtual IEnumerable<Site> AllSites()
        {
            return CascadedChildSites(null);
        }
        private IEnumerable<Site> CascadedChildSites(Site site)
        {
            var childSites = ChildSites(site);
            foreach (var child in childSites.AsEnumerable())
            {
                childSites = childSites.Concat(CascadedChildSites(child));
            }
            return childSites;
        }

        public virtual IEnumerable<Site> ChildSites(Site site)
        {
            List<Site> list = new List<Site>();
            //if the site is null, get the root sites.
            string baseDir = Site.RootBasePhysicalPath;
            if (site != null)
            {
                baseDir = site.ChildSitesBasePhysicalPath;
            }
            if (Directory.Exists(baseDir))
            {
                foreach (var dir in IO.IOUtility.EnumerateDirectoriesExludeHidden(baseDir))
                {
                    if (File.Exists(Path.Combine(dir.FullName, PathResource.SettingFileName)))
                    {
                        list.Add(new Site(site, dir.Name));
                    }
                }
            }
            return list;
        }

        public virtual IEnumerable<Site> AllRootSites()
        {
            return ChildSites(null);
        }

        #endregion

        #region Online/Offline

        public virtual void Offline(Site site)
        {
            var offlineFile = GetOfflineFile(site);
            if (!File.Exists(offlineFile))
            {
                File.WriteAllText(offlineFile, "The site is offline, please remove this file to take it online.".Localize());
            }
        }
        public virtual void Online(Site site)
        {
            var offlineFile = GetOfflineFile(site);
            if (File.Exists(offlineFile))
            {
                File.Delete(offlineFile);
            }
        }
        public virtual bool IsOnline(Site site)
        {
            var offlineFile = GetOfflineFile(site);
            return !File.Exists(offlineFile);
        }
        private string GetOfflineFile(Site site)
        {
            return Path.Combine(site.PhysicalPath, Offline_File);
        }

        #endregion

        #region Create
        /// <summary>
        /// 1. Extract the site files.
        /// 2. Create and initialize the repository if the repository doest not exsits.
        /// </summary>
        /// <param name="parentSite"></param>
        /// <param name="siteName"></param>
        /// <param name="packageStream"></param>
        /// <returns></returns>
        public virtual Site Create(Site parentSite, string siteName, System.IO.Stream packageStream, CreateSiteSetting createSitSetting)
        {
            Site site = new Site(parentSite, siteName);
            if (site.Exists())
            {
                throw new KoobooException("The site already exists.");
            }
            using (ZipFile zipFile = ZipFile.Read(packageStream))
            {
                var action = ExtractExistingFileAction.OverwriteSilently;
                zipFile.ExtractAll(site.PhysicalPath, action);

                if (parentSite == null)
                {
                    baseDir.UpdateFileLink(site.PhysicalPath, siteName, createSitSetting.Repository);
                }

                site = CreateSiteRepository(site, createSitSetting.Repository);
                CreateMembership(site, createSitSetting.Membership);
            }
            return site;
        }

        private Site CreateSiteRepository(Site site, string newRepositoryName)
        {
            //Create the repository if the repository does not exists.
            site = site.AsActual();
            if (!string.IsNullOrEmpty(newRepositoryName))
            {
                if (CMS.Content.Services.ServiceFactory.RepositoryManager.Get(newRepositoryName) == null)
                {
                    var repositoryFile = GetSiteRelatedFile(site, new[] { ContentDatabaseFileName, site.Repository });
                    if (!string.IsNullOrEmpty(repositoryFile) && File.Exists(repositoryFile))
                    {
                        using (FileStream fs = new FileStream(repositoryFile, FileMode.Open, FileAccess.Read))
                        {
                            CMS.Content.Services.ServiceFactory.RepositoryManager.Create(newRepositoryName, fs);
                        }
                        try
                        {
                            File.Delete(repositoryFile);
                        }
                        catch (Exception e)
                        {
                            Kooboo.HealthMonitoring.Log.LogException(e);
                        }
                        site.Repository = newRepositoryName;
                    }
                    else if (site.Parent != null)
                    {
                        site.Repository = site.Parent.AsActual().Repository;
                    }
                }
            }
            Save(site);
            foreach (var childSite in ChildSites(site))
            {
                CreateSiteRepository(childSite, GetChildSiteRepositoryName(childSite, newRepositoryName));
            }

            return site;
        }
        private Site CreateMembership(Site site, string membershipName)
        {
            //Create the repository if the repository does not exists.
            site = site.AsActual();

            if (!string.IsNullOrEmpty(membershipName))
            {
                var membership = new Kooboo.CMS.Membership.Models.Membership(membershipName).AsActual();
                if (membership == null)
                {
                    var membershipFile = GetSiteRelatedFile(site, MembershipFileName);
                    if (!string.IsNullOrEmpty(membershipFile) && File.Exists(membershipFile))
                    {
                        using (FileStream fs = new FileStream(membershipFile, FileMode.Open, FileAccess.Read))
                        {
                            _membershipProvider.Import(membershipName, fs);
                        }
                        site.Membership = membershipName;
                        try
                        {
                            File.Delete(membershipFile);
                        }
                        catch (Exception e)
                        {
                            Kooboo.HealthMonitoring.Log.LogException(e);
                        }
                    }
                    else if (site.Parent != null)
                    {
                        site.Membership = site.Parent.AsActual().Membership;
                    }
                    else
                    {
                        site.Membership = null;
                    }
                }
            }
            Save(site);
            foreach (var childSite in ChildSites(site))
            {
                CreateMembership(childSite, membershipName);
            }

            return site;
        }

        private string GetChildSiteRepositoryName(Site childSite, string newRepositoryName)
        {
            childSite = childSite.AsActual();
            if (Kooboo.CMS.Content.Services.ServiceFactory.RepositoryManager.Get(childSite.Repository) == null)
            {
                return childSite.Repository;
            }
            else
            {
                return newRepositoryName + "_" + childSite.Name;
            }
        }
        public virtual void Initialize(Site site)
        {
            //Initialize 
            Providers.HtmlBlockProvider.InitializeHtmlBlocks(site);
            Providers.PageProvider.InitializePages(site);
            _labelProvider.InitializeLabels(site);

            foreach (var sub in Providers.SiteProvider.ChildSites(site))
            {
                Providers.SiteProvider.Initialize(sub.AsActual());
            }
        }

        #endregion

        #region Export

        private void ExportLabels(Site site, bool includeSubSites)
        {

            _labelProvider.ExportLabelsToDisk(site);

            if (includeSubSites)
            {
                foreach (var sub in Providers.SiteProvider.ChildSites(site))
                {
                    ExportLabels(sub, includeSubSites);
                }
            }

        }

        private void ExportPages(Site site, bool includeSubSites)
        {
            Providers.PageProvider.ExportPagesToDisk(site);
            if (includeSubSites)
            {
                foreach (var sub in Providers.SiteProvider.ChildSites(site))
                {
                    ExportPages(sub, includeSubSites);
                }
            }
        }

        private void ExportHtmlBlocks(Site site, bool includeSubSites)
        {
            Providers.HtmlBlockProvider.ExportHtmlBlocksToDisk(site);
            if (includeSubSites)
            {
                foreach (var sub in Providers.SiteProvider.ChildSites(site))
                {
                    ExportHtmlBlocks(sub, includeSubSites);
                }
            }
        }
        /// <summary>
        /// 1. export repository as a zip file.
        /// 2. offline the site.
        /// 3. zip the site directory as a zip file.
        /// 4. online the site.
        /// </summary>
        /// <param name="site"></param>
        /// <param name="outputStream"></param>
        public virtual void Export(Site site, System.IO.Stream outputStream, bool includeDatabase, bool includeSubSites)
        {
            ISiteProvider siteProvider = Providers.SiteProvider;

            //export the data to disk.
            ExportLabels(site, includeSubSites);
            ExportPages(site, includeSubSites);
            ExportHtmlBlocks(site, includeSubSites);

            using (ZipFile zipFile = new ZipFile(Encoding.UTF8))
            {
                //zipFile.ZipError += new EventHandler<ZipErrorEventArgs>(zipFile_ZipError);
                var ignores = "name != *\\~versions\\*.* and name != *\\.svn\\*.* and name != *\\_svn\\*.*";
                var dirs = Directory.EnumerateDirectories(site.PhysicalPath);
                if (!includeSubSites)
                {
                    dirs = dirs.Where(it => !Path.GetFileName(it).EqualsOrNullEmpty("Sites", StringComparison.OrdinalIgnoreCase));
                }
                foreach (var dir in dirs)
                {
                    zipFile.AddSelectedFiles(ignores, dir, Path.GetFileName(dir), true);
                }
                zipFile.AddFiles(Directory.EnumerateFiles(site.PhysicalPath), "");

                if (includeDatabase)
                {
                    ExportSiteRepository(site, site, zipFile, includeSubSites);
                    ExportSiteMembership(site, site, zipFile, includeSubSites);
                }

                zipFile.ZipErrorAction = ZipErrorAction.Skip;

                zipFile.Save(outputStream);
            }
        }

        private void ExportSiteRepository(Site rootSiteExported, Site site, ZipFile zipFile, bool includeSubSites)
        {
            site = site.AsActual();

            if (!string.IsNullOrEmpty(site.Repository))
            {
                if (site.Parent == null || (site.Parent != null
                    && !site.Parent.AsActual().Repository.EqualsOrNullEmpty(site.Repository, StringComparison.CurrentCultureIgnoreCase)))
                {
                    MemoryStream ms = new MemoryStream();

                    Kooboo.CMS.Content.Services.ServiceFactory.RepositoryManager.Export(site.Repository, ms);

                    ms.Position = 0;

                    var entryName = GetSiteRelatedEntryName(rootSiteExported, site, ContentDatabaseFileName);

                    if (zipFile.ContainsEntry(entryName))
                    {
                        zipFile.RemoveEntry(entryName);
                    }
                    zipFile.AddEntry(entryName, ms);
                }
            }
            if (includeSubSites)
            {
                foreach (var childSite in ChildSites(site))
                {
                    ExportSiteRepository(rootSiteExported, childSite, zipFile, includeSubSites);
                }
            }
        }

        private void ExportSiteMembership(Site rootSiteExported, Site site, ZipFile zipFile, bool includeSubSites)
        {
            site = site.AsActual();

            if (!string.IsNullOrEmpty(site.Membership))
            {
                if (site.Parent == null || (site.Parent != null
                    && !site.Parent.AsActual().Membership.EqualsOrNullEmpty(site.Membership, StringComparison.CurrentCultureIgnoreCase)))
                {
                    MemoryStream ms = new MemoryStream();

                    _membershipProvider.Export(site.GetMembership(), ms);

                    if (ms.Length > 0)
                    {
                        ms.Position = 0;

                        var entryName = GetSiteRelatedEntryName(rootSiteExported, site, MembershipFileName);

                        if (zipFile.ContainsEntry(entryName))
                        {
                            zipFile.RemoveEntry(entryName);
                        }
                        zipFile.AddEntry(entryName, ms);
                    }

                }
            }
            if (includeSubSites)
            {
                foreach (var childSite in ChildSites(site))
                {
                    ExportSiteMembership(rootSiteExported, childSite, zipFile, includeSubSites);
                }
            }
        }
        private static string GetSiteRelatedEntryName(Site rootSiteExported, Site site, string entryName)
        {
            entryName = entryName + ".zip";
            if (site.Parent != null)
            {
                //单独导出子站点的时候，目录需要移除的是导出站点路径
                var removeLength = rootSiteExported.VirtualPath.Length + 1;
                if (site.VirtualPath.Length > removeLength)
                {
                    entryName = UrlUtility.Combine(site.VirtualPath.Remove(0, removeLength), entryName);
                }
            }
            return entryName;
        }
        private static string GetSiteRelatedFile(Site site, params string[] entryNames)
        {
            foreach (var name in entryNames)
            {
                if (!string.IsNullOrEmpty(name))
                {
                    var entryName = name + ".zip";
                    entryName = Path.Combine(site.PhysicalPath, entryName);
                    if (File.Exists(entryName))
                    {
                        return entryName;
                    }
                }
            }
            return null;
        }
        #endregion

    }
}
