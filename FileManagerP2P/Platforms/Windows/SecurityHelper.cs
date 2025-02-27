using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FileManagerP2P.Platforms.Windows
{
    internal static class SecurityHelper
    {
        public static bool IsSymbolicLink(string path)
        {
            var attr = File.GetAttributes(path);
            return attr.HasFlag(FileAttributes.ReparsePoint);
        }

        public static void ValidateFilePermissions(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    throw new SecurityException("Symbolic links are not supported");
                }
                try
                {
                    FileSystemSecurity security;
                    if (File.Exists(path))
                    {
                        security = FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
                    }
                    else if (Directory.Exists(path))
                    {
                        security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path));
                    }
                    else
                    {
                        throw new FileNotFoundException("File not found", path);
                    }

                    var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
                    var identity = WindowsIdentity.GetCurrent();
                    var principal = new WindowsPrincipal(identity);

                    var hasAccess = false;
                    foreach (FileSystemAccessRule rule in rules)
                    {
                        if (identity.User?.Equals(rule.IdentityReference) == true ||
                            principal.IsInRole((SecurityIdentifier)rule.IdentityReference))
                        {
                            if (rule.FileSystemRights.HasFlag(FileSystemRights.Read | FileSystemRights.Write))
                            {
                                hasAccess = true;
                                break;
                            }
                        }
                    }

                    if (!hasAccess)
                    {
                        throw new UnauthorizedAccessException("Insufficient permissions to access the file");
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not UnauthorizedAccessException)
                {
                    throw new SecurityException("Unable to verify file permissions due to security configuration", ex);
                }
            }
            catch (SecurityException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SecurityException("Unable to verify file permissions", ex);
            }
        }
    }
}
