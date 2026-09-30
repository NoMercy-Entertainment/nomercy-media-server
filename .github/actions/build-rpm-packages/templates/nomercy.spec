Name: nomercy
Version: {{VERSION}}
Release: 1%{?dist}
Summary: NoMercy MediaServer - complete installation
License: Proprietary
URL: https://nomercy.tv
Source0: nomercy-payload.tar.gz
Source1: NoMercy-MediaServer.desktop
Source2: icon.png
Source3: nomercymediaserver.service
Source4: nomercylauncher.service
Source5: README
BuildArch: x86_64
BuildRoot: %{_tmppath}/%{name}-%{version}-%{release}-root

# rpmbuild adds a Requires for every library a bundled ELF file links. Files that never load on
# x86_64 Linux must add none, or dnf refuses the install ("nothing provides ..."):
# - runtimes/<rid>/native for other platforms (NoMercy.Storage.csproj links libnfs for every RID);
#   the linux-arm64 copy asks for ld-linux-aarch64.so.1.
# - libcoreclrtraceptprovider.so (.NET LTTng tracing, loaded only when LTTng is installed) asks for
#   liblttng-ust.so.0, which current Fedora does not ship.
%global __requires_exclude_from ^/opt/nomercy/runtimes/(linux-arm|linux-musl-|osx-|win-)
%global __requires_exclude ^liblttng-ust\\.so\\.0

Requires: glibc
Recommends: systemd

%description
Modern Media Server Solution with server, launcher, and CLI tool.
All components share a single .NET runtime for efficient disk usage.

%prep
%setup -q -c -T
tar xzf %{SOURCE0}

%build

%install
rm -rf %{buildroot}

# Install shared runtime and executables
mkdir -p %{buildroot}/opt/nomercy
cp -R * %{buildroot}/opt/nomercy/
chmod 755 %{buildroot}/opt/nomercy/NoMercyMediaServer
chmod 755 %{buildroot}/opt/nomercy/NoMercyApp
chmod 755 %{buildroot}/opt/nomercy/NoMercyLauncher
chmod 755 %{buildroot}/opt/nomercy/nomercy

# Symlinks in /usr/bin
mkdir -p %{buildroot}/usr/bin
ln -s /opt/nomercy/NoMercyMediaServer %{buildroot}/usr/bin/nomercymediaserver
ln -s /opt/nomercy/NoMercyApp %{buildroot}/usr/bin/nomercyapp
ln -s /opt/nomercy/NoMercyLauncher %{buildroot}/usr/bin/nomercylauncher
ln -s /opt/nomercy/nomercy %{buildroot}/usr/bin/nomercy

# Desktop files
mkdir -p %{buildroot}/usr/share/applications
install -m 644 %{SOURCE1} %{buildroot}/usr/share/applications/ 2>/dev/null || true

# Icon
mkdir -p %{buildroot}/usr/share/icons/hicolor/scalable/apps
install -m 644 %{SOURCE2} %{buildroot}/usr/share/icons/hicolor/scalable/apps/NoMercy-MediaServer.png

# Systemd service files (real checked-in templates, staged as RPM sources)
mkdir -p %{buildroot}/usr/lib/systemd/user
install -m 644 %{SOURCE3} %{buildroot}/usr/lib/systemd/user/nomercymediaserver.service
install -m 644 %{SOURCE4} %{buildroot}/usr/lib/systemd/user/nomercylauncher.service

# Documentation
mkdir -p %{buildroot}/usr/share/doc/%{name}
install -m 644 %{SOURCE5} %{buildroot}/usr/share/doc/%{name}/README

%files
%defattr(-,root,root,-)
/opt/nomercy
/usr/bin/nomercymediaserver
/usr/bin/nomercyapp
/usr/bin/nomercylauncher
/usr/bin/nomercy
%attr(644,root,root) /usr/share/applications/*.desktop
%attr(644,root,root) /usr/share/icons/hicolor/scalable/apps/NoMercy-MediaServer.png
%attr(644,root,root) /usr/lib/systemd/user/nomercymediaserver.service
%attr(644,root,root) /usr/lib/systemd/user/nomercylauncher.service
%doc /usr/share/doc/%{name}/README

%post
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q /usr/share/icons/hicolor || true
fi
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database /usr/share/applications || true
fi
systemctl --user daemon-reload >/dev/null 2>&1 || true

%preun
if [ "$1" -eq 0 ] ; then
    systemctl --user stop nomercymediaserver.service >/dev/null 2>&1 || true
    systemctl --user disable nomercymediaserver.service >/dev/null 2>&1 || true
    systemctl --user stop nomercylauncher.service >/dev/null 2>&1 || true
    systemctl --user disable nomercylauncher.service >/dev/null 2>&1 || true
fi

%postun
if [ "$1" -eq 0 ] ; then
    if command -v gtk-update-icon-cache >/dev/null 2>&1; then
        gtk-update-icon-cache -q /usr/share/icons/hicolor || true
    fi
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database /usr/share/applications || true
    fi
fi

%clean
rm -rf %{buildroot}

%changelog
* {{CHANGELOG_DATE}} NoMercy <support@nomercy.tv> - {{VERSION}}-1
- Unified package with shared runtime
