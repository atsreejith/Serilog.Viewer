import { NavLink, Outlet } from "react-router-dom";
import { LayoutDashboard, ScrollText, Radio } from "lucide-react";
import logoIcon from "@/log-viewer-icon.png";
import { useAppStore } from "@/store/appStore";

const BASE_NAV_ITEMS = [
  { to: "/dashboard", icon: LayoutDashboard, label: "Dashboard" },
  { to: "/explorer", icon: ScrollText, label: "Log Explorer" },
];

const LIVE_TAIL_NAV = { to: "/live", icon: Radio, label: "Live Tail" };

export function MainLayout() {
  const liveTailEnabled = useAppStore((s) => s.liveTailEnabled);
  const navItems = liveTailEnabled
    ? [...BASE_NAV_ITEMS, LIVE_TAIL_NAV]
    : BASE_NAV_ITEMS;
  const appVersion = `v${__APP_VERSION__}`;

  return (
    <div className="flex h-screen bg-[#0d1117] text-[#e6edf3] overflow-hidden">
      {/* Sidebar */}
      <nav className="w-14 shrink-0 flex flex-col items-center py-4 gap-2 border-r border-[#21262d] bg-[#161b22]">
        {/* Logo */}
        <div className="mb-4 shrink-0">
          <img
            src={logoIcon}
            alt="Serilog Viewer"
            className="w-9 h-9 rounded-xl"
          />
        </div>

        {navItems.map(({ to, icon: Icon, label }) => (
          <NavLink
            key={to}
            to={to}
            title={label}
            className={({ isActive }) =>
              `w-9 h-9 flex items-center justify-center rounded-lg transition-colors ${
                isActive
                  ? "bg-[#58a6ff]/20 text-[#58a6ff]"
                  : "text-[#484f58] hover:text-[#8b949e] hover:bg-[#21262d]"
              }`
            }
          >
            <Icon size={18} />
          </NavLink>
        ))}

        <div className="flex-1" />

        <div
          title={`Version ${__APP_VERSION__}`}
          className="text-[10px] leading-none font-medium text-[#6e7681]"
        >
          {appVersion}
        </div>

        {/* GitHub link */}
        <a
          href="https://github.com/atsreejith/Serilog.Viewer"
          target="_blank"
          rel="noopener noreferrer"
          title="GitHub"
          className="w-9 h-9 flex items-center justify-center rounded-lg text-[#484f58] hover:text-[#8b949e] hover:bg-[#21262d] transition-colors"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            width="18"
            height="18"
            viewBox="0 0 24 24"
            fill="currentColor"
          >
            <path d="M12 0C5.37 0 0 5.37 0 12c0 5.3 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61-.546-1.385-1.335-1.755-1.335-1.755-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 21.795 24 17.295 24 12c0-6.63-5.37-12-12-12z" />
          </svg>
        </a>
      </nav>

      {/* Main area */}
      <main className="flex-1 flex flex-col overflow-hidden">
        <Outlet />
      </main>
    </div>
  );
}
