﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ProjectSystem.Interop;

namespace NuGet.VisualStudio
{
    class NuGetAwareProjectPackageRepository : IPackageRepository, IPackageReferenceRepository2
    {
        INuGetPackageManager _project;
        ISharedPackageRepository _repo;

        public NuGetAwareProjectPackageRepository(INuGetPackageManager project, ISharedPackageRepository sourceRepository)
        {
            _project = project;
            _repo = sourceRepository;
        }

        public string Source
        {
            get { return "ICanSupportNuGet"; }
        }

        public PackageSaveModes PackageSaveMode
        {
            get
            {
                throw new NotSupportedException();
            }
            set
            {
                throw new NotSupportedException();
            }
        }

        public bool SupportsPrereleasePackages
        {
            get { return true; }
        }

        public IQueryable<IPackage> GetPackages()
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            var task = _project.GetInstalledPackagesAsync(cts.Token);
            task.Wait();

            List<IPackage> packages = new List<IPackage>();
            foreach (object item in task.Result)
            {
                var moniker = item as INuGetPackageMoniker;
                if (moniker != null)
                {
                    packages.Add(new DataServicePackage()
                    {
                        Id = moniker.Id,
                        Version = moniker.Version
                    });

                    continue;
                }

                var fileName = item as string;
                if (item != null)
                {
                    packages.Add(new OptimizedZipPackage(fileName));
                }
            }
            return packages.AsQueryable();
        }

        public void AddPackage(IPackage package)
        {
            // no-op
        }

        public void RemovePackage(IPackage package)
        {
            // no-op
        }

        public PackageReference GetPackageReference(string packageId)
        {
            var package = GetPackages().Where(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (package == null)
            {
                return null;
            }

            var packageReference = new PackageReference(
                package.Id,
                package.Version,
                versionConstraint: null,
                targetFramework: null,
                isDevelopmentDependency: false);
            return packageReference;
        }

        public IEnumerable<PackageReference> GetPackageReferences(string packageId)
        {
            var packageReference = GetPackageReference(packageId);
            if (packageReference == null)
            {
                return Enumerable.Empty<PackageReference>();
            }
            else
            {
                return new[] { packageReference };
            }
        }
    }
}
