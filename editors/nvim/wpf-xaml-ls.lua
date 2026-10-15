-- wpf-xaml-ls for Neovim 0.11+ (uses vim.lsp.config / vim.lsp.enable; no plugins required).
--
-- Install: copy this file to your config's after/plugin/ folder, e.g.
--   Linux/macOS: ~/.config/nvim/after/plugin/wpf-xaml-ls.lua
--   Windows:     %LOCALAPPDATA%\nvim\after\plugin\wpf-xaml-ls.lua
-- and put the server on PATH: `dotnet tool install -g WpfXaml.LanguageServer --prerelease`
-- (or point `cmd` below at a local build).

local settings = {
  -- Hide lemminx's diagnostics in .xaml buffers. Both servers report XML syntax errors, so they show twice;
  -- lemminx keeps providing formatting, folding and the outline either way.
  hide_lemminx_diagnostics_in_xaml = true,
}

-- Neovim has no filetype for .xaml. Treat it as XML (highlighting, tree-sitter, lemminx) unless your config
-- already decided; the server below accepts both "xml" and "xaml".
if not vim.filetype.match({ filename = 'x.xaml' }) then
  vim.filetype.add({ extension = { xaml = 'xml' } })
end

local function is_xaml(bufnr)
  return vim.api.nvim_buf_get_name(bufnr):lower():match('%.xaml$') ~= nil
end

vim.lsp.config('wpf_xaml_ls', {
  cmd = { 'wpf-xaml-ls' },
  filetypes = { 'xml', 'xaml' },
  -- Start only for .xaml files (not plain XML), rooted at the nearest solution, then project, then git root.
  -- The server loads the solution it finds there, and loads any other project on demand when you open one
  -- of its XAML files.
  root_dir = function(bufnr, on_dir)
    if not is_xaml(bufnr) then
      return
    end
    local function has(pattern)
      return function(name) return name:match(pattern) ~= nil end
    end
    on_dir(vim.fs.root(bufnr, has('%.slnx?$')) or vim.fs.root(bufnr, has('%.csproj$')) or vim.fs.root(bufnr, '.git')
      or vim.fs.dirname(vim.api.nvim_buf_get_name(bufnr)))
  end,
  init_options = {
    -- solution = 'path/to/App.sln', -- load this instead of the one found under the root
    -- loadProjects = false,          -- framework types only (no MSBuild)
    -- frameworkVersion = 8,          -- WPF targeting pack used before/without a project
  },
})

vim.lsp.enable('wpf_xaml_ls')

if settings.hide_lemminx_diagnostics_in_xaml then
  vim.api.nvim_create_autocmd('LspAttach', {
    group = vim.api.nvim_create_augroup('wpf-xaml-ls-lemminx', { clear = true }),
    callback = function(args)
      local client = vim.lsp.get_client_by_id(args.data.client_id)
      if client and client.name == 'lemminx' and is_xaml(args.buf) then
        vim.diagnostic.enable(false, { bufnr = args.buf, ns_id = vim.lsp.diagnostic.get_namespace(client.id) })
      end
    end,
  })
end
