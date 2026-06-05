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
      </nav>

      {/* Main area */}
      <main className="flex-1 flex flex-col overflow-hidden">
        <Outlet />
      </main>
    </div>
  );
}
