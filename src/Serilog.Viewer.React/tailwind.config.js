/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        background: '#0d1117',
        surface: '#161b22',
        border: '#30363d',
        'border-subtle': '#21262d',
        accent: '#58a6ff',
        'text-primary': '#e6edf3',
        'text-secondary': '#8b949e',
        'text-muted': '#484f58',
        level: {
          verbose: '#8b949e',
          debug: '#58a6ff',
          info: '#3fb950',
          warning: '#d29922',
          error: '#f85149',
          fatal: '#bc8cff',
        },
      },
      fontFamily: {
        mono: ['JetBrains Mono', 'Fira Code', 'Consolas', 'monospace'],
      },
    },
  },
  plugins: [],
}
