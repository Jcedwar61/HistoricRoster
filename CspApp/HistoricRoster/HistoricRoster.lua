-- Historic Roster: CSP app for servers running HistoricRosterPlugin (AssettoServer).
-- Opens from the apps sidebar on the right. Reads rosters from the plugin's HTTP API,
-- sends admin actions as chat commands, and gets missing REAL cars/tracks with RosterTool.

-- ------------------------------------------------------------------ CSP compatibility
-- The app runs on any CSP build that runs Lua apps, release or preview/beta. Anything a build may not have (newer
-- functions, newer car and session fields) is looked up before it's used: on a build without it, only that one
-- feature turns off and everything else keeps working.

-- Does this CSP have t[key]? (Reading a missing field of a car or session state is an error, hence the pcall.)
local function has(t, key)
  if t == nil then return false end
  local ok, v = pcall(function() return t[key] end)
  return ok and v ~= nil
end

-- Calls t[key](...) if this CSP has it. Returns false if it hasn't or the call failed, else true and the results.
local function try(t, key, ...)
  if not has(t, key) then return false end
  return pcall(t[key], ...)
end

-- A field of a car or session state, or nil if this CSP's state hasn't got it (checked once per object and field).
local fieldOk = setmetatable({}, { __mode = 'k' })
local function field(obj, key)
  if obj == nil then return nil end
  local known = fieldOk[obj]
  if not known then known = {} fieldOk[obj] = known end
  local ok = known[key]
  if ok == nil then
    ok = pcall(function() return obj[key] end)
    known[key] = ok
  end
  if ok then return obj[key] end
  return nil
end

local clock = os.preciseClock or os.clock

-- Text drawn on screen: DirectWrite where this CSP has it, else the plain font.
local function drawText(text, size, pos, color)
  if ui.dwriteDrawText then ui.dwriteDrawText(text, size, pos, color) else ui.drawText(text, pos, color) end
end

local settingDefaults = {
  serverUrl = '',          -- manual override, e.g. http://127.0.0.1:8081
  toolPath = '',           -- manual override for RosterTool.exe
  category = '',
  watchMinutes = 30,
  classBox = true,         -- multi-class races: show your class position on screen while you drive
}
local storageOk, storage = try(ac, 'storage', settingDefaults)
local settings = storageOk and storage or settingDefaults -- (without ac.storage: not kept between sessions)

local appDir = ac.dirname()
local carsDir = ac.getFolder(ac.FolderID.ContentCars)
local tracksDir = ac.getFolder(ac.FolderID.ContentTracks)

local state = {
  query = '',
  list = nil,         -- /rosters response
  listError = nil,
  loadingList = false,
  selectedId = nil,
  roster = nil,       -- /roster/{id} response
  rosterError = nil,
  loadingRoster = false,
  active = nil,
  log = {},
  toolRunning = false,
  f1Year = tostring(os.date('%Y') - 1), f1Round = '',
  nascarYear = '2019', nascarSeries = '1', nascarRace = '',
  -- /roster_load options (Admin tab)
  loadOrder = 'StartingGrid', loadPercent = '100', loadWeather = true, loadStandIns = false,
}

local gridOrders = { 'StartingGrid', 'FinishingOrder', 'AsListed' }

local colors = {
  ok = rgbm(0.45, 0.85, 0.45, 1),
  bad = rgbm(1, 0.45, 0.4, 1),
  dim = rgbm(0.7, 0.7, 0.7, 1),
  head = rgbm(1, 0.8, 0.35, 1),
}

-- ------------------------------------------------------------------ helpers

local function addLog(line)
  for part in tostring(line):gmatch('[^\r\n]+') do
    table.insert(state.log, part)
  end
  while #state.log > 200 do table.remove(state.log, 1) end
end

local function urlEncode(s)
  return (tostring(s):gsub('[^%w%-_%.~]', function(c) return string.format('%%%02X', string.byte(c)) end))
end

local function baseUrl()
  if settings.serverUrl ~= '' then return settings.serverUrl:gsub('/+$', '') end
  local okIp, ip = try(ac, 'getServerIP')
  local okPort, port = try(ac, 'getServerPortHTTP')
  port = okPort and tonumber(port) or nil
  if okIp and ip and ip ~= '' and port and port > 0 then return 'http://' .. ip .. ':' .. port end
  return nil
end

local function getJson(path, callback)
  local base = baseUrl()
  if not base then callback('Not connected to a server (or set the server address under Settings).') return end
  web.get(base .. '/historicroster/' .. path, function(err, response)
    if err then callback(err) return end
    if response.status ~= 200 then callback('HTTP ' .. response.status .. ' (is HistoricRosterPlugin enabled on this server?)') return end
    local ok, data = pcall(JSON.parse, response.body)
    if not ok or data == nil then callback('Invalid response from server') return end
    callback(nil, data)
  end)
end

local function refreshList()
  state.loadingList = true
  local q = 'rosters?limit=100&q=' .. urlEncode(state.query) .. '&category=' .. urlEncode(settings.category)
  getJson(q, function(err, data)
    state.loadingList = false
    state.listError = err
    if data then state.list = data end
  end)
  getJson('active', function(err, data) if data then state.active = data end end)
end

local function loadRoster(id)
  state.selectedId = id
  state.loadingRoster = true
  state.roster = nil
  getJson('roster/' .. urlEncode(id), function(err, data)
    state.loadingRoster = false
    state.rosterError = err
    state.roster = data
  end)
end

local function sendCommand(cmd)
  addLog('> ' .. cmd)
  local ok, sent = try(ac, 'sendChatMessage', cmd)
  if not ok or sent == false then addLog('Could not send (not online, or sending too fast).') end
end

-- The roster seat bookings apply to: the one waiting for a restart, else the one running.
local function loadedRosterId()
  local a = state.active
  if not a then return nil end
  if a.pending then return a.pending.id end
  if a.running then return a.running.id end
  return nil
end

-- Re-read the grid (and status) shortly after a command, so bookings show up.
local function refreshSoon()
  setTimeout(function()
    getJson('active', function(err, data) if data then state.active = data end end)
    -- Reload the grid in place (no "Loading..." flash).
    local id = state.selectedId
    if id then
      getJson('roster/' .. urlEncode(id), function(err, data)
        if data and state.selectedId == id then state.roster = data end
      end)
    end
  end, 1.5)
end

local function myName()
  return ac.getDriverName(0) or ''
end

-- Is a needed real car/track installed on THIS PC?
local function installedLocally(item)
  if not item.folder then return false end
  if item.kind == 'track' then
    local dir = tracksDir .. '/' .. item.folder
    if item.layout then return io.dirExists(dir .. '/' .. item.layout) end
    return io.dirExists(dir)
  end
  return io.dirExists(carsDir .. '/' .. item.folder)
end

local function missingLocally()
  local list = {}
  if state.roster and state.roster.needs then
    for _, item in ipairs(state.roster.needs) do
      if not installedLocally(item) then table.insert(list, item) end
    end
  end
  return list
end

local function toolPath()
  if settings.toolPath ~= '' then return settings.toolPath end
  local p = appDir .. '/tool/RosterTool.exe'
  if io.fileExists(p) then return p end
  return nil
end

local function runTool(items)
  local exe = toolPath()
  if not exe then
    addLog('RosterTool not found. Run "RosterTool install-app" (it copies the tool here) or set its path in Settings.')
    return
  end
  local file = appDir .. '/pending_items.json'
  io.save(file, JSON.stringify(items))
  if not has(os, 'runConsoleProcess') then
    addLog('This CSP build can\'t start programs from an app. Run this yourself:')
    addLog('"' .. exe .. '" get "' .. file .. '" --watch ' .. tostring(settings.watchMinutes))
    return
  end
  state.toolRunning = true
  addLog('Running RosterTool for ' .. #items .. ' item(s)...')
  os.runConsoleProcess({
    filename = exe,
    arguments = { 'get', file, '--watch', tostring(settings.watchMinutes) },
    workingDirectory = exe:match('^(.*)[/\\]') or appDir,
    terminateWithScript = false,
    dataCallback = function(err, data) addLog(data) end,
  }, function(err, result)
    state.toolRunning = false
    if err then addLog('RosterTool: ' .. tostring(err)) else addLog('RosterTool finished.') end
  end)
end

-- Server messages (replies to /roster_ commands) are copied into the app log; they still show in chat.
try(ac, 'onChatMessage', function(message, senderCarIndex)
  if senderCarIndex == -1 then addLog(message) end
  return false
end)

-- ------------------------------------------------------------------ tabs

local function tabRaces()
  local q, changed, enter = ui.inputText('Search (e.g. "le mans 2008", "monaco")', state.query, ui.InputTextFlags.None)
  state.query = q
  ui.sameLine()
  if ui.button('Search') or enter then refreshList() end

  local categories = state.list and state.list.categories or {}
  ui.setNextItemWidth(160)
  ui.combo('Series', settings.category == '' and 'All' or settings.category, ui.ComboFlags.None, function()
    if ui.selectable('All', settings.category == '') then settings.category = '' refreshList() end
    for _, c in ipairs(categories) do
      if ui.selectable(c, settings.category == c) then settings.category = c refreshList() end
    end
  end)
  ui.sameLine()
  if ui.button('Refresh') then refreshList() end

  if state.active then
    local run = state.active.running
    if run then
      ui.textColored('Running: ' .. run.name .. (run.laps and ('  (' .. run.laps .. ' laps)') or ''), colors.ok)
      if run.conditions then ui.textDisabled('  Real conditions: ' .. run.conditions) end
    end
    if state.active.pending then ui.textColored('Waiting for restart: ' .. state.active.pending.name, colors.head) end
  end
  if state.loadingList then ui.text('Loading...') end
  if state.listError then ui.textColored(state.listError, colors.bad) return end
  if not state.list then
    if not state.loadingList then refreshList() end
    return
  end

  ui.textDisabled(string.format('%d rosters', state.list.total or 0))
  ui.childWindow('rosterList', vec2(ui.availableSpaceX(), ui.availableSpaceY()), true, function()
    for _, r in ipairs(state.list.rosters or {}) do
      local label = string.format('%s  (%d cars, %s)##%s', r.name, r.cars or 0, r.category or '?', r.id)
      if ui.selectable(label, state.selectedId == r.id) then loadRoster(r.id) end
      if ui.itemHovered() then ui.setTooltip((r.track or '') .. '\n' .. r.id) end
    end
  end)
end

local function tabGrid()
  if state.loadingRoster then ui.text('Loading...') return end
  if state.rosterError then ui.textColored(state.rosterError, colors.bad) return end
  local r = state.roster
  if not r then ui.text('Pick a race in the Races tab.') return end

  ui.textColored(r.name, colors.head)
  if r.series then ui.textDisabled(r.series) end
  if r.track then
    local t = r.track
    local where = t.folder and (t.folder .. (t.layout and ('/' .. t.layout) or '')) or 'no AC track known'
    ui.text('Track: ' .. (t.name or '?') .. '  [' .. where .. ']')
    if not t.installed then ui.textColored('  Not installed on the server', colors.bad) end
  end
  if r.realLaps then ui.text('Real distance: ' .. r.realLaps .. ' laps') end
  if r.source then ui.textDisabled('Source: ' .. r.source) end
  if r.notes then ui.textWrapped(r.notes) end

  -- Seat booking works on the race loaded on the server (running, or waiting for a restart).
  local bookable = loadedRosterId() == r.id
  local mineBooked = false
  for _, e in ipairs(r.entries or {}) do
    if e.bookedBy and e.bookedBy == myName() then mineBooked = true end
  end
  if bookable then
    ui.textColored('This race is loaded on the server: book a driver to race in their seat (from the next restart).', colors.head)
    if mineBooked then
      if ui.button('Free my seat') then sendCommand('/roster_unseat') refreshSoon() end
      ui.sameLine()
    end
    if ui.button('Who booked what') then sendCommand('/roster_seats') end
  end
  ui.separator()

  ui.childWindow('grid', vec2(ui.availableSpaceX(), ui.availableSpaceY()), false, function()
    for i, e in ipairs(r.entries or {}) do
      local pos = e.position and ('P' .. e.position) or (e.status or '-')
      local line = string.format('%2d. %-5s #%-3s %s', i, pos, e.number or '', e.driver or '')
      if bookable then
        if e.bookedBy then
          local mine = e.bookedBy == myName()
          ui.textColored(mine and 'Yours ' or 'Taken ', mine and colors.ok or colors.dim)
          if ui.itemHovered() then ui.setTooltip('Booked by ' .. e.bookedBy) end
        else
          if ui.button('Book##seat' .. i, vec2(48, 0)) then
            sendCommand('/roster_seat ' .. (e.driver or ''))
            refreshSoon()
          end
          if ui.itemHovered() then ui.setTooltip('Race as ' .. (e.driver or '?') .. ' (seat held for your Steam account from the next restart)') end
        end
        ui.sameLine()
      end
      ui.text(line)
      ui.sameLine(bookable and 360 or 300)
      local car = (e.car or '?')
      if e.model then
        local local_ok = io.dirExists(carsDir .. '/' .. e.model)
        ui.textColored(car, local_ok and colors.ok or colors.bad)
        if ui.itemHovered() then
          ui.setTooltip((e.team or '') .. '\nAC car: ' .. e.model .. (e.skin and ('\nLivery: ' .. e.skin) or '') ..
            (local_ok and '\nInstalled on this PC' or '\nNot installed on this PC'))
        end
      else
        ui.textColored(car .. ' (no AC car known)', colors.bad)
      end
    end
  end)
end

local function tabContent()
  local r = state.roster
  if not r then ui.text('Pick a race in the Races tab.') return end
  ui.textColored(r.name, colors.head)
  ui.textWrapped('The real cars and track are used. Missing ones should be installed; the server only fills them with marked same-class stand-ins if its admin turned that on.')
  ui.separator()

  local missing = missingLocally()
  if #missing == 0 then
    ui.textColored('Everything for this race is installed on this PC.', colors.ok)
  else
    ui.text(#missing .. ' item(s) missing on this PC:')
    if not state.toolRunning then
      if ui.button('Download and install all missing') then runTool(missing) end
      if ui.itemHovered() then
        ui.setTooltip('Direct links are downloaded and installed. Pages that need your login (overtake.gg) open in your browser;\n' ..
          'archives you download there are installed automatically from your Downloads folder.')
      end
    else
      ui.textColored('RosterTool is working - see the log below.', colors.head)
    end
    ui.childWindow('missing', vec2(ui.availableSpaceX(), math.max(120, ui.availableSpaceY() * 0.45)), true, function()
      for i, item in ipairs(missing) do
        ui.text((item.kind == 'track' and 'Track: ' or 'Car: ') .. (item.label or item.folder or '?'))
        ui.sameLine(ui.availableSpaceX() - 70)
        if ui.button('Open##dl' .. i, vec2(70, 0)) then try(os, 'openURL', item.url) end
        if ui.itemHovered() then ui.setTooltip(item.url) end
        -- Other sites to search (servers with an older plugin send no sources).
        local others = {}
        for _, s in ipairs(item.sources or {}) do
          if s.url ~= item.url then table.insert(others, s) end
        end
        if #others > 0 then
          ui.textDisabled('   also try:')
          for j, s in ipairs(others) do
            ui.sameLine()
            if ui.button(s.name .. '##src' .. i .. '_' .. j) then try(os, 'openURL', s.url) end
            if ui.itemHovered() then ui.setTooltip(s.url) end
          end
        end
      end
    end)
  end

  if r.missing and #r.missing > 0 then
    ui.textColored(#r.missing .. ' item(s) missing on the server (admin: install them there too).', colors.bad)
  end
end

local function tabAdmin()
  local r = state.roster
  ui.textWrapped('These send chat commands; the server only accepts them from admins (log in with /admin <password>). Replies appear in the log.')
  ui.separator()
  if r then
    ui.text('Selected: ' .. r.name)

    -- /roster_load options
    ui.setNextItemWidth(140)
    ui.combo('Grid##order', state.loadOrder, ui.ComboFlags.None, function()
      for _, o in ipairs(gridOrders) do
        if ui.selectable(o, state.loadOrder == o) then state.loadOrder = o end
      end
    end)
    ui.sameLine(); ui.setNextItemWidth(50)
    state.loadPercent = ui.inputText('% of real distance##pct', state.loadPercent)
    if ui.itemHovered() then
      ui.setTooltip((r.realLaps and ('Real race: ' .. r.realLaps .. ' laps. ') or '') .. '0 = keep the server\'s race length.')
    end
    if ui.checkbox('Real weather and start time', state.loadWeather) then state.loadWeather = not state.loadWeather end
    ui.sameLine()
    if ui.checkbox('Stand-ins for missing cars', state.loadStandIns) then state.loadStandIns = not state.loadStandIns end
    if ui.itemHovered() then ui.setTooltip('An installed car of the same class takes a missing real car\'s seat, marked as a stand-in.') end

    local pct = tonumber(state.loadPercent)
    if ui.button('Load this race (restart needed)') then
      local cmd = ('/roster_load %s %s'):format(r.id, state.loadOrder)
      if pct then cmd = cmd .. ' ' .. math.floor(pct) .. '%' end
      cmd = cmd .. (state.loadWeather and ' weather' or ' noweather')
      cmd = cmd .. (state.loadStandIns and ' standins' or ' nostandins')
      sendCommand(cmd)
      refreshSoon()
    end
    if not pct then
      ui.sameLine(); ui.textColored('Distance must be a number', colors.bad)
    end
    ui.sameLine()
    if ui.button('Show missing on server') then sendCommand('/roster_content ' .. r.id) end
  else
    ui.textDisabled('Pick a race in the Races tab to load it.')
  end
  if ui.button('Undo last load') then sendCommand('/roster_restore') end
  ui.sameLine()
  if ui.button('Status') then sendCommand('/roster_active') end
  ui.separator()

  ui.text('Import an F1 race (real grid and result):')
  ui.setNextItemWidth(70); state.f1Year = ui.inputText('Year##f1', state.f1Year)
  ui.sameLine(); ui.setNextItemWidth(60); state.f1Round = ui.inputText('Round (empty = list)##f1', state.f1Round)
  ui.sameLine()
  if ui.button('Import##f1') then sendCommand(('/roster_f1 %s %s'):format(state.f1Year, state.f1Round)) refreshList() end

  ui.text('Import a NASCAR race (2017 on):')
  ui.setNextItemWidth(70); state.nascarYear = ui.inputText('Year##ns', state.nascarYear)
  ui.sameLine(); ui.setNextItemWidth(40); state.nascarSeries = ui.inputText('Series 1-3##ns', state.nascarSeries)
  ui.sameLine(); ui.setNextItemWidth(50); state.nascarRace = ui.inputText('Race # (empty = list)##ns', state.nascarRace)
  ui.sameLine()
  if ui.button('Import##ns') then sendCommand(('/roster_nascar %s %s %s'):format(state.nascarYear, state.nascarSeries, state.nascarRace)) refreshList() end
end

local function tabSettings()
  local base = baseUrl()
  ui.text('Server API: ' .. (base and (base .. '/historicroster') or 'not connected'))
  settings.serverUrl = ui.inputText('Server address override (http://ip:port)', settings.serverUrl)
  settings.toolPath = ui.inputText('RosterTool.exe path override', settings.toolPath)
  ui.text('RosterTool: ' .. (toolPath() or 'not found'))
  ui.text('Your cars folder: ' .. carsDir)
  ui.text('Your tracks folder: ' .. tracksDir)
  if ui.checkbox('Multi-class races: show my class position on screen', settings.classBox) then settings.classBox = not settings.classBox end
  ui.separator()
  local guide = appDir .. '/USER_GUIDE.md'
  if ui.button('Open the user guide') then
    if not try(os, 'openTextFile', guide) then try(os, 'openURL', guide) end
  end
  if ui.itemHovered() then ui.setTooltip(guide) end
end

-- ------------------------------------------------------------------ watched AI race
-- The Historic Roster program writes [HISTORIC_ROSTER] WATCH=1 into race.ini for a race to watch. Only then, this:
--  * turns on CSP's autopilot for the user car, so every car is driven by AI;
--  * turns the ideal racing line off;
--  * runs its own TV director camera (ac.grabCamera), which can follow ANY car. AC's own track cameras only
--    follow the user car during a live session, so they can't show the race.
-- (manifest LAZY = NONE: this runs from the start of the session even if the window is never opened.)

local watch = {
  checked = false, on = false,
  session = -1, engaged = false, userOff = false, wantAi = true,
  follow = 'grid',     -- 'grid' (rotate through the whole field), 'leader', 'battle' or 'manual'
  manualTarget = nil,  -- car index picked in the running order
  target = nil,        -- car index the director is showing
  partner = nil,       -- the car it is fighting (kept in frame too), or nil
  targetHeld = 0,      -- seconds the current target has been shown
  holdFor = 15,        -- seconds to stay on the current car in 'grid' mode
  lastShown = {},      -- car index -> clock() when it was last put on screen
  fresh = true,        -- a race session that hasn't got going yet: the camera starts on your seat
  skip = false,        -- "Next car" pressed: move on even from a car the director would stay with
  timer = 0,
}

local director = {
  enabled = true,
  style = 'auto',  -- 'auto', 'tv', 'onboard', 'chase', 'heli'
  cam = nil, error = nil,
  shot = nil,      -- { kind, age, length, progress, pos }
  pos = vec3(), dir = vec3(0, 0, 1), fov = 30, up = vec3(0, 1, 0),
  -- Everything below is smoothed with critically damped springs (smooth start and stop, no overshoot) on top of the
  -- car's own motion, so a car at a steady speed is followed with no lag at all and only bumps and twitches are
  -- filtered out.
  track = vec3(), trackErrV = vec3(), -- the point followed on the car (smoothed), and its spring's velocity
  vel = vec3(),                -- the filmed car's velocity, lightly smoothed
  posV = vec3(),               -- chase / helicopter camera position spring's velocity
  flyVel = vec3(),             -- helicopter's own (slowly changing) velocity
  aimOffset = vec3(), aimOffsetV = vec3(), -- offset from the followed point to the point aimed at (rival, lead room)
  heading = vec3(0, 0, 1), heading1 = vec3(0, 0, 1), -- level heading of the car (two-stage smoothing)
  rival = 0, rivalV = 0,       -- 0 alone .. 1 with a rival in the picture
  logFov = math.log(30), logFovV = 0, goalRate = 0, lastGoal = nil, -- zoom (smoothed in log scale)
  panFor = 0, panTotal = 1,    -- seconds left of a pan from one car to another (no cut), and its whole length
  lastTarget = nil,
}

-- Critically damped spring towards 0 (exact for any frame time, so it never overshoots or blows up): x is the offset
-- from the goal, v its velocity. Returns the new x, v.
local function spring(x, v, omega, dt)
  local e = math.exp(-omega * dt)
  local t = v + omega * x
  return (x + t * dt) * e, (v - omega * t * dt) * e
end

-- The same for a vec3 offset and velocity (changed in place).
local function springVec(x, v, omega, dt)
  x.x, v.x = spring(x.x, v.x, omega, dt)
  x.y, v.y = spring(x.y, v.y, omega, dt)
  x.z, v.z = spring(x.z, v.z, omega, dt)
end

-- Moves `pos` (in place) after `goal`, which moves at `goalVel`: carried along with the goal, plus a spring that
-- takes up any remaining difference (posV is the spring's velocity).
local followTmp = vec3()
local function follow(pos, posV, goal, goalVel, omega, dt)
  local x = followTmp:set(pos):addScaled(goalVel, dt):sub(goal)
  springVec(x, posV, omega, dt)
  pos:set(goal):add(x)
end

local ideal = { wasOn = nil, timer = 0, tries = 0 }

local UP = vec3(0, 1, 0)
local DOWN = vec3(0, -1, 0)

-- race.ini, when its [HISTORIC_ROSTER] section belongs to the race that's loaded; nil otherwise. Content Manager keeps
-- sections it doesn't know when it writes race.ini for another race, and that race must never get this one's watch
-- flag, Safety Car, classes or real result. The program writes the race's track and car count there to tell. Offline
-- only: online, race.ini isn't ours. Checked once (AC reads race.ini once, when it starts).
local ourRace = { checked = false, cfg = nil }
local function ourRaceConfig()
  if ourRace.checked then return ourRace.cfg end
  ourRace.checked = true
  local sim = ac.getSim()
  if sim.isOnlineRace then return nil end
  local ok, cfg = try(ac.INIConfig, 'raceConfig')
  if not ok or not cfg then return nil end
  local function str(key)
    local ok2, v = pcall(function() return cfg:get('HISTORIC_ROSTER', key, '') end)
    return ok2 and type(v) == 'string' and v or ''
  end
  if tonumber(str('CARS')) ~= sim.carsCount then return nil end
  local track = str('TRACK'):lower()
  if track == '' then return nil end
  local okId, id = try(ac, 'getTrackID')
  if okId and type(id) == 'string' and id:lower() ~= track then return nil end
  ourRace.cfg = cfg
  return cfg
end

-- The seat picked for a watched race, which the camera starts on (race.ini CAR_n index). It's not always CAR_0: AC
-- opens its pit menu for CAR_0 whenever that car pits, so the program gives CAR_0 to a car that never retires.
local cameraSeat = nil
local function cameraCar()
  if cameraSeat then return cameraSeat end
  cameraSeat = 0
  local cfg = ourRaceConfig()
  if cfg then
    local ok, v = pcall(function() return cfg:get('HISTORIC_ROSTER', 'CAMERA_CAR', 0) end)
    local n = ok and tonumber(v)
    if n and n >= 0 and n < ac.getSim().carsCount then cameraSeat = math.floor(n) end
  end
  return cameraSeat
end

local function readWatchFlag()
  local cfg = ourRaceConfig()
  if not cfg then return false end
  local ok2, v = pcall(function() return cfg:get('HISTORIC_ROSTER', 'WATCH', 0) end)
  return ok2 and tonumber(v) == 1
end

local function userDriving()
  return not watch.wantAi or watch.userOff
end

-- The autopilot on the user's seat. A CSP without it: the user switches it with AC's own autopilot key.
local function setAutopilot(on)
  if not has(physics, 'setCarAutopilot') then watch.noAutopilot = true return end
  if on then pcall(physics.setCarAutopilot, true, true) else pcall(physics.setCarAutopilot, false) end
end

-- ------------------------------------------------------------------ which car to film

-- Cars that are in the race. The Safety Car (written by the program as SAFETY_CAR, or named "Safety Car") is never
-- filmed: it isn't racing, and following it loses the race.
local raceCars = { loaded = false, safety = {} }
local function loadRaceCars()
  raceCars.loaded = true
  local cfg = ourRaceConfig()
  if cfg then
    local ok2, v = pcall(function() return cfg:get('HISTORIC_ROSTER', 'SAFETY_CAR', -1) end)
    if ok2 and tonumber(v) and tonumber(v) >= 0 then raceCars.safety[tonumber(v)] = true end
  end
  for i = 0, ac.getSim().carsCount - 1 do
    local name = (ac.getDriverName(i) or ''):lower()
    if name == 'safety car' or name:find('safety car', 1, true) then raceCars.safety[i] = true end
  end
end
local function isRaceCar(i)
  if not raceCars.loaded then loadRaceCars() end
  return not raceCars.safety[i]
end

-- ------------------------------------------------------------------ classes (multi-class races)
-- For a race with several classes on one grid (LMP, GT, prototypes ...) the program writes [HISTORIC_ROSTER]
-- CLASS_1..n (fastest class first) and CAR_n_CLASS into race.ini. Offline only: online, race.ini isn't ours.
local classes = { loaded = false, list = {}, of = {} }
local function loadClasses()
  classes.loaded = true
  local cfg = ourRaceConfig()
  if not cfg then return end
  local function str(key)
    local ok2, v = pcall(function() return cfg:get('HISTORIC_ROSTER', key, '') end)
    return ok2 and type(v) == 'string' and v ~= '' and v or nil
  end
  for k = 1, 64 do
    local name = str('CLASS_' .. k)
    if not name then break end
    table.insert(classes.list, name)
  end
  if #classes.list < 2 then classes.list = {} return end
  for i = 0, ac.getSim().carsCount - 1 do classes.of[i] = str('CAR_' .. i .. '_CLASS') end
end

local function multiClass()
  if not classes.loaded then loadClasses() end
  return #classes.list > 1
end

-- A car's class ('' for a car without one in a multi-class race).
local function classOf(i)
  if not classes.loaded then loadClasses() end
  return classes.of[i] or ''
end

-- The classes to list: the known ones fastest first, then '' (cars without a class).
local function classGroups()
  local list = {}
  for _, name in ipairs(classes.list) do table.insert(list, name) end
  table.insert(list, '')
  return list
end

-- Race cars sorted by race position.
local function runningOrder()
  local sim = ac.getSim()
  local list = {}
  for i = 0, sim.carsCount - 1 do
    local c = ac.getCar(i)
    if c and field(c, 'isConnected') ~= false and isRaceCar(i) then table.insert(list, c) end
  end
  table.sort(list, function(a, b) return (a.racePosition or 999) < (b.racePosition or 999) end)
  return list
end

-- Laps + track progress, for gaps between cars.
local function distanceRun(c) return (c.lapCount or 0) + (c.splinePosition or 0) end

-- Worth filming: racing on track, not in the pit lane, retired or parked. (Before the start everyone counts.)
local function onTrack(c)
  if not c or field(c, 'isInPitlane') or field(c, 'isRetired') or not isRaceCar(c.index) then return false end
  return not ac.getSim().isSessionStarted or (c.speedKmh or 0) > 15
end

-- Still racing: on track and hasn't taken the chequered flag (a car on its cool-down lap isn't in a fight).
local function racing(c)
  return onTrack(c) and not field(c, 'isRaceFinished')
end

-- ------------------------------------------------------------------ sessions
-- A watched race weekend can be the race only, qualifying and the race, or practice, qualifying and the race. Each
-- session is filmed the way TV films it: practice and qualifying follow cars on their flying laps (there are no
-- battles, only traffic), the race starts on your seat and ends on the cars taking the chequered flag.

-- 'practice', 'qualify' or 'race' (a CSP that can't tell: 'race', as before sessions were told apart).
local function sessionKind()
  local t = tonumber(field(ac.getSim(), 'raceSessionType'))
  if t == 1 then return 'practice' end
  if t == 2 then return 'qualify' end
  if t == nil or t == 3 then return 'race' end
  return 'practice' -- (hotlap, time attack ...: never written by the program; filmed like practice)
end

-- Race distance in laps (0 for a timed race or when it can't be told), looked up once per session.
local sessionLaps = { index = -1, laps = 0 }
local function raceLaps()
  local sim = ac.getSim()
  if sessionLaps.index ~= sim.currentSessionIndex then
    sessionLaps.index, sessionLaps.laps = sim.currentSessionIndex, 0
    local ok, s = try(ac, 'getSession', sim.currentSessionIndex)
    local n = ok and s and tonumber(field(s, 'laps')) or 0
    if n <= 0 then
      local cfg = ourRaceConfig()
      if cfg then
        local ok3, v = pcall(function() return cfg:get('HISTORIC_ROSTER', 'RACE_LAPS', 0) end)
        n = ok3 and tonumber(v) or 0
      end
    end
    sessionLaps.laps = n
  end
  return sessionLaps.laps
end

-- Each car's laps, updated twice a second: car index -> { pitLap, lastLap, pushing, doneAt, doneMs, donePush,
-- finishedAt }. pitLap is the lap count when it was last in the pit lane, so lapCount == pitLap is an out lap.
local lapState = {}
local flag = { out = false } -- the chequered flag is out (the winner has finished)

-- A push lap in practice or qualifying: out on track, past its out lap, and not a slow lap (more than a couple of
-- seconds down on its best: a cool-down or in lap).
local function pushingLap(c, l)
  if not onTrack(c) or (c.lapCount or 0) <= l.pitLap then return false end
  local best, perf = field(c, 'bestLapTimeMs') or 0, field(c, 'performanceMeter')
  return best <= 0 or perf == nil or perf < 2
end

local function updateLaps()
  local now = clock()
  local out = false
  for i = 0, ac.getSim().carsCount - 1 do
    local c = ac.getCar(i)
    if c and isRaceCar(i) then
      local laps = c.lapCount or 0
      local l = lapState[i]
      -- (Fewer laps than last time: the session was restarted.)
      if not l or laps < l.lastLap then l = { pitLap = -1, lastLap = laps } lapState[i] = l end
      if laps > l.lastLap then
        -- Crossed the line: the lap just completed, and whether it was a push lap.
        l.doneAt, l.doneMs, l.donePush = now, field(c, 'previousLapTimeMs'), l.pushing
      end
      l.lastLap = laps
      if field(c, 'isInPitlane') then l.pitLap = laps end
      l.pushing = pushingLap(c, l)
      if not l.finishedAt and field(c, 'isRaceFinished') then l.finishedAt = now end
      if l.finishedAt then out = true end
    end
  end
  flag.out = out
end

-- Lap time as 1:23.456 (tenths only: 1:23.4), nil for none.
local function lapTimeText(ms, tenths)
  ms = tonumber(ms)
  if not ms or ms <= 0 then return nil end
  local s = ms / 1000
  if tenths then return string.format('%d:%04.1f', math.floor(s / 60), math.floor(s % 60 * 10) / 10) end
  return string.format('%d:%06.3f', math.floor(s / 60), s % 60)
end

-- The leader, or the first car behind it that is out on track.
local function leaderOnTrack(order)
  for _, c in ipairs(order) do if onTrack(c) then return c.index end end
  return order[1] and order[1].index
end

-- One class out of a running order.
local function classOrder(order, name)
  local list = {}
  for _, c in ipairs(order) do if classOf(c.index) == name then table.insert(list, c) end end
  return list
end

-- A car's position in its class (1 = class leader), the cars in the class and the class name; nil when the race
-- has one class.
local function classPosition(order, idx)
  if not multiClass() then return nil end
  local name, pos, n = classOf(idx), nil, 0
  for _, c in ipairs(order) do
    if classOf(c.index) == name then
      n = n + 1
      if c.index == idx then pos = n end
    end
  end
  return pos, n, (name == '' and 'Other' or name)
end

local BATTLE_M = 30      -- a car this close behind another one (metres) is fighting it
local BATTLE_END_M = 55  -- ... and the fight is over when the gap grows past this
local MIN_HOLD = 7       -- seconds a car stays on screen at least (unless it stops racing)

-- Gap in metres from the car ahead (a) to the car behind (b) on track, nil when it can't be told.
local function gapMeters(a, b)
  local len = field(ac.getSim(), 'trackLengthM') or 0
  if len < 100 then return nil end
  local d = (distanceRun(a) - distanceRun(b)) * len
  return d >= 0 and d or nil
end

-- Every battle on track: a car within BATTLE_M of the car ahead of it, both racing. Scored so the closest fights
-- for the highest places come first; a train of cars nose to tail counts as one bigger battle. In a multi-class
-- race only cars of the same class fight (a prototype lapping a GT car isn't a battle), for class places.
local function battles(order)
  local list = {}
  if not ac.getSim().isSessionStarted then return list end
  local groups = { order }
  if multiClass() then
    groups = {}
    for _, name in ipairs(classGroups()) do table.insert(groups, classOrder(order, name)) end
  end
  for gi, g in ipairs(groups) do
    for k = 2, #g do
      local a, b = g[k - 1], g[k]
      if racing(a) and racing(b) then
        local gap = gapMeters(a, b)
        if gap and gap < BATTLE_M then
          -- Multi-class: the place in the class, a little lower for each slower class.
          local place = multiClass() and k + 2 * (gi - 1) or (b.racePosition or 20)
          local score = (BATTLE_M - gap) / BATTLE_M * 3 + 3 / math.max(1, place)
          local last = list[#list]
          if last and last.behind == a.index then
            -- Same train: one battle, filmed at its front where it's decided.
            last.score, last.size = last.score + score * 0.5, last.size + 1
          else
            table.insert(list, { ahead = a.index, behind = b.index, gap = gap, score = score, size = 2 })
          end
        end
      end
    end
  end
  table.sort(list, function(x, y) return x.score > y.score end)
  return list
end

-- The current target is still in a fight: the car ahead or behind it (in its class) is within BATTLE_END_M.
local function stillFighting(order, idx)
  if multiClass() then order = classOrder(order, classOf(idx)) end
  for k, c in ipairs(order) do
    if c.index == idx then
      local ahead, behind = order[k - 1], order[k + 1]
      if ahead and racing(ahead) and (gapMeters(ahead, c) or math.huge) < BATTLE_END_M then return true, ahead.index end
      if behind and racing(behind) and (gapMeters(c, behind) or math.huge) < BATTLE_END_M then return true, behind.index end
      return false
    end
  end
  return false
end

-- Whole grid without a battle on: the car that has gone longest without being on screen (the leader now and then),
-- so over a race every car gets shown. (In a multi-class race "the leader" is one of the class leaders.)
local function gridPick(order)
  local leader = leaderOnTrack(order)
  if multiClass() then
    local leaders = {}
    for _, name in ipairs(classGroups()) do
      for _, c in ipairs(classOrder(order, name)) do
        if racing(c) then table.insert(leaders, c.index) break end
      end
    end
    if #leaders > 0 then leader = leaders[math.random(#leaders)] end
  end
  if math.random() < 0.25 and leader ~= watch.target then return leader end
  -- Cars still racing first; once the flag is out, the ones on their cool-down lap only when nobody else is left.
  for pass = 1, 2 do
    local best, bestTime = nil, math.huge
    for _, c in ipairs(order) do
      if c.index ~= watch.target and (pass == 1 and racing(c) or pass == 2 and onTrack(c)) then
        local t = watch.lastShown[c.index] or -1
        if t < bestTime then best, bestTime = c.index, t end
      end
    end
    if best then return best end
  end
  return leader
end

local function setTarget(want, partner)
  watch.partner = partner
  if want == nil then return end
  if want ~= watch.target then
    watch.target, watch.targetHeld, watch.offTrackFor = want, 0, 0
    local c = ac.getCar(want)
    watch.pickedLap = c and c.lapCount or 0 -- (the lap it was picked on: a qualifying lap is followed to its end)
    try(ac, 'focusCar', want) -- HUD and sounds follow the shown car where AC allows it
  end
  watch.lastShown[want] = clock() -- (while it's on screen)
end

-- ---- practice and qualifying
-- No racing, so no battles: what's worth watching is a car on a push lap. Like TV: a qualifying lap is followed to the
-- line and its time shown before the next car; practice rotates through the cars out on track. Cars higher up the
-- times, cars up on their best lap, and cars not on screen for a while come first. When nobody is out on track: a
-- car moving in the pit lane (on its way out), else the pit box of the car last shown.

local function lapScore(order, c, quali)
  local place = classPosition(order, c.index) or c.racePosition or 20
  if (field(c, 'bestLapTimeMs') or 0) <= 0 then place = math.max(place, 8) end -- no time yet
  local score = 4 / math.max(1, place) * (quali and 1.5 or 1)
  local seen = watch.lastShown[c.index]
  score = score + math.min(90, seen and clock() - seen or 90) / 30
  local perf = field(c, 'performanceMeter')
  if perf and (field(c, 'bestLapTimeMs') or 0) > 0 and perf < -0.05 then score = score + 2 end -- up on its best
  if (c.splinePosition or 0) > 0.8 then score = score - 1.5 end -- too little of the lap left to be worth joining
  if c.index == watch.target then score = score - 4 end -- (its lap was just shown: someone else's turn)
  return score + math.random() * 0.5
end

local function chooseLapTarget(order, leave, skip)
  local quali = sessionKind() == 'qualify'
  local cur = watch.target ~= nil and ac.getCar(watch.target) or nil
  local l = cur and lapState[cur.index]
  if cur and l and not leave and not skip then
    -- Just completed a push lap: stay a few seconds for its time and place (the caption shows them).
    if l.donePush and l.doneAt and clock() - l.doneAt < 5 then return setTarget(cur.index, nil) end
    -- On a push lap: in qualifying all the way to the line; in practice for a while, and to the line when it's close.
    -- (Only the lap it was picked on: after that the next car gets its turn, unless nobody else is on a push lap.)
    if l.pushing and (cur.lapCount or 0) == watch.pickedLap then
      local stay = quali and watch.targetHeld < 150
        or watch.targetHeld < watch.holdFor or (cur.splinePosition or 0) > 0.75 and watch.targetHeld < 90
      if stay then return setTarget(cur.index, nil) end
    elseif onTrack(cur) and watch.targetHeld < MIN_HOLD then
      return setTarget(cur.index, nil)
    end
  end

  if watch.follow == 'leader' then
    -- The fastest car, whenever it is out on track.
    local fastest = order[1]
    if fastest and onTrack(fastest) and not (skip and fastest.index == watch.target) then return setTarget(fastest.index, nil) end
  end

  local best, bestScore = nil, -math.huge
  for _, c in ipairs(order) do
    local cl = lapState[c.index]
    if cl and cl.pushing and not (skip and c.index == watch.target) then
      local score = lapScore(order, c, quali)
      if score > bestScore then best, bestScore = c, score end
    end
  end
  watch.holdFor = 25 + math.random() * 15
  if best then
    if best.index == watch.target then watch.pickedLap, watch.targetHeld = best.lapCount or 0, 0 end -- (its next lap)
    return setTarget(best.index, nil)
  end

  -- Nobody on a push lap: the cars out on track (out laps, in laps), in turn.
  if cur and not leave and not skip and onTrack(cur) and watch.targetHeld < 20 then return setTarget(cur.index, nil) end
  local pick = gridPick(order)
  local pc = pick ~= nil and ac.getCar(pick) or nil
  if pc and onTrack(pc) then return setTarget(pick, nil) end
  for _, c in ipairs(order) do
    if onTrack(c) and not (skip and c.index == watch.target) then return setTarget(c.index, nil) end
  end

  -- Nobody out on track: a car on its way down the pit lane, else stay in the pits with the car last shown.
  if cur and field(cur, 'isInPitlane') and (cur.speedKmh or 0) > 3 and not skip then return setTarget(cur.index, nil) end
  for _, c in ipairs(order) do
    if field(c, 'isInPitlane') and (c.speedKmh or 0) > 3 and c.index ~= watch.target then return setTarget(c.index, nil) end
  end
  if cur and isRaceCar(cur.index) then return setTarget(cur.index, nil) end
  setTarget(order[1].index, nil)
end

-- ---- the end of the race
-- The leader's last lap is followed to the line, the winner held for a few seconds after the flag, then the next cars
-- to take the flag (class winners and close finishes first), each for a moment after it crosses. Returns true when
-- it picked the car.
local function chooseFinish(order)
  local n = raceLaps()
  if n <= 0 then return false end
  local cur = watch.target ~= nil and ac.getCar(watch.target) or nil
  if cur and field(cur, 'isRaceFinished') then
    local l = lapState[cur.index]
    local since = l and l.finishedAt and clock() - l.finishedAt
    local winner = cur.racePosition == 1 or classPosition(order, cur.index) == 1
    if since and since < (winner and 8 or 4) then
      setTarget(cur.index, nil)
      return true
    end
  end
  if not flag.out then
    local lead = order[1]
    if lead and onTrack(lead) and (lead.lapCount or 0) == n - 1
      and ((lead.splinePosition or 0) > 0.5 or watch.target == lead.index) then
      local _, partner = stillFighting(order, lead.index)
      setTarget(lead.index, partner)
      return true
    end
    return false
  end
  local best, bestScore = nil, 0
  for _, c in ipairs(order) do
    if racing(c) and (c.splinePosition or 0) > 0.82 then
      local place = classPosition(order, c.index) or c.racePosition or 20
      local score = 4 / math.max(1, place) + (stillFighting(order, c.index) and 1.5 or 0) + (c.index == watch.target and 1 or 0)
      if score > bestScore then best, bestScore = c, score end
    end
  end
  if best then
    local _, partner = stillFighting(order, best.index)
    setTarget(best.index, partner)
    return true
  end
  return false
end

local function chooseTarget(dt)
  watch.targetHeld = watch.targetHeld + dt
  local order = runningOrder()
  if #order == 0 then return end
  updateLaps()
  local kind, sim = sessionKind(), ac.getSim()
  local skip = watch.skip
  watch.skip = false

  -- Never stay on a car that isn't racing (pitted, retired, stopped) or on the Safety Car.
  local current = watch.target ~= nil and ac.getCar(watch.target) or nil
  if current and not onTrack(current) then watch.offTrackFor = (watch.offTrackFor or 0) + dt else watch.offTrackFor = 0 end
  local leave = skip or watch.target ~= nil and (not isRaceCar(watch.target) or (watch.follow ~= 'manual' and watch.offTrackFor >= 1))

  if watch.follow == 'manual' and watch.manualTarget and isRaceCar(watch.manualTarget) then
    local partner = kind == 'race' and select(2, stillFighting(order, watch.manualTarget)) or nil
    return setTarget(watch.manualTarget, partner)
  end

  if kind ~= 'race' then return chooseLapTarget(order, leave, skip) end

  -- The start: on your seat (the car the camera starts on) until the field is away. (Back on the grid after the race
  -- had started: a restart, so the start again.)
  if watch.wasStarted and not sim.isSessionStarted then watch.fresh = true end
  watch.wasStarted = sim.isSessionStarted
  local seat = cameraCar()
  if watch.fresh and watch.follow ~= 'leader' and not skip and isRaceCar(seat) and ac.getCar(seat)
    and (not sim.isSessionStarted or (director.raceClock or 0) < 12) then
    local _, partner = stillFighting(order, seat)
    return setTarget(seat, partner)
  end
  watch.fresh = false

  if not skip and chooseFinish(order) then return end

  local fights = battles(order)
  local best = fights[1]
  if skip and best and (best.behind == watch.target or best.ahead == watch.target) then best = fights[2] end

  if watch.follow == 'leader' then
    local leader = leaderOnTrack(order)
    local _, partner = stillFighting(order, leader)
    return setTarget(leader, partner)
  end

  -- 'grid' and 'battle': a battle on track always comes first.
  if not leave and watch.target ~= nil then
    local fighting, partner = stillFighting(order, watch.target)
    -- Stay with a fight while it lasts (up to 40 s, unless a much better one starts elsewhere).
    if fighting and (watch.targetHeld < 40 or not best) then
      if not best or watch.targetHeld < 8 or best.score < 2.5 or best.behind == watch.target or best.ahead == watch.target then
        return setTarget(watch.target, partner)
      end
    end
    -- Never cut away from a car within a few seconds of picking it (unless it stopped racing): a new car every
    -- couple of seconds makes the pictures jumpy.
    if watch.targetHeld < MIN_HOLD then return setTarget(watch.target, fighting and partner or nil) end
  end
  if best then return setTarget(best.behind, best.ahead) end

  -- No battle anywhere.
  if watch.follow == 'battle' then
    local leader = leaderOnTrack(order)
    if not (skip and leader == watch.target) then return setTarget(leader, nil) end
  end
  if watch.target ~= nil and watch.targetHeld < watch.holdFor and not leave then return setTarget(watch.target, nil) end
  watch.holdFor = 14 + math.random() * 10
  setTarget(gridPick(order), nil)
end

-- Ideal line off for the watched race (AC's "Ideal Line" hotkey command toggles it).
local function toggleIdealLine()
  for _, name in ipairs({ 'Ideal Line', 'IDEAL_LINE', 'idealLine' }) do
    local ok, done = try(ac, 'trySimKeyPressCommand', name)
    if ok and done then return true end
  end
  return false
end

local function updateIdealLine(dt)
  local shown = field(ac.getSim(), 'idealLineShown')
  if shown == nil then return end -- (this CSP doesn't say whether the ideal line is shown: leave it alone)
  if ideal.wasOn == nil then ideal.wasOn = shown end
  ideal.timer = ideal.timer + dt
  if ideal.timer < 1.5 then return end
  ideal.timer = 0
  local wantShown = userDriving() and ideal.wasOn
  if shown ~= wantShown and ideal.tries < 12 then
    ideal.tries = ideal.tries + 1
    toggleIdealLine()
  end
end

-- ------------------------------------------------------------------ real result
-- In a watched race the program writes each car's real finishing position (0 = didn't finish) and real laps
-- completed. The race is steered to that result:
--  * every car's pace (AI level and grip) is nudged toward its real place among the cars still running, gently
--    at first and harder in the last quarter, when cars still out of place also lose their collisions (so a car
--    that must get past isn't blocked) and cars too far forward get their throttle limited;
--  * a car that really retired goes into the pits at the same point of the race (scaled to this race's length)
--    and is parked there;
--  * AC's own random AI retirements are switched off, so no other car drops out.

local real = {
  available = false, enabled = true, loaded = false,
  cars = {},          -- car index -> { pos, laps, retireAt, crossed, baseLevel, pitting, pitSince, retired }
  realLaps = 0, raceLaps = 0,
  outOfPlace = 0, status = nil,
}

local function loadRealResult()
  real.loaded = true
  local cfg = ourRaceConfig()
  if not cfg then return end
  local function num(key, default)
    local ok2, v = pcall(function() return cfg:get('HISTORIC_ROSTER', key, default) end)
    return ok2 and tonumber(v) or default
  end
  if num('REAL_RESULT', 0) ~= 1 then return end
  real.available = true
  real.realLaps, real.raceLaps = num('REAL_LAPS', 0), num('RACE_LAPS', 0)
  for i = 0, ac.getSim().carsCount - 1 do
    local pos = num('CAR_' .. i .. '_POS', -1)
    if pos >= 0 then
      local e = { pos = pos, laps = num('CAR_' .. i .. '_LAPS', 0) }
      if pos == 0 and real.realLaps > 0 and real.raceLaps > 0 and i ~= 0 then
        -- Retire at the same share of the race distance as in reality (never right on the grid). Never the user's car
        -- (CAR_0): AC would open its pit menu for it; the program makes it a car that finished.
        e.retireAt = math.max(0.4, e.laps / real.realLaps * real.raceLaps)
      end
      real.cars[i] = e
      pcall(physics.preventAIFromRetiring, i)
    end
  end
end

-- Laps run (completed laps + progress on the current one). Before a car first crosses the start line its
-- track progress is ~0.99, so that part only counts once it has crossed.
local function lapsRun(c, e)
  local s = c.splinePosition or 0
  if (c.lapCount or 0) > 0 or s < 0.5 then e.crossed = true end
  return (c.lapCount or 0) + (e.crossed and s or 0)
end

-- A car stuck on track after a spin or a tangle loses laps that no steering gives back (AC ranks by completed laps,
-- and nothing can set them). So after 8 s standing still on track it's put back on the racing line where it was, at
-- a spot with no car within 25 m. Only where nobody sees it: never the car being filmed or its rival, and more than
-- 250 m from the camera at both ends. Only once it has been racing (a race session counts as started while the grid
-- still waits for the lights), and never across the finish line (the lap it was on wouldn't count).
local stuckCars = {}
local function hiddenFromCamera(i, pos)
  if i == watch.target or i == watch.partner or pos == nil then return false end
  local ok, cam = try(ac, 'getCameraPosition')
  return ok and cam ~= nil and pos:distance(cam) > 250
end

local function rescueStuck(i, c, step)
  local sim = ac.getSim()
  local s = stuckCars[i]
  if not s then s = { raced = false, still = 0 } stuckCars[i] = s end
  if not sim.isSessionStarted then s.raced, s.still = false, 0 return false end -- (on the grid, or a restart)
  if (c.speedKmh or 0) > 40 then s.raced = true end
  if not s.raced or field(c, 'isInPitlane') or (c.speedKmh or 0) >= 5 then s.still = 0 return false end
  s.still = s.still + step
  if s.still < 8 or not hiddenFromCamera(i, c.position) then return false end
  s.still = 0
  local len = math.max(field(sim, 'trackLengthM') or 5000, 500)
  local here = c.splinePosition or 0
  for _, k in ipairs({ 1, 2, 3, 4, 6, 8, 10, 12, -2, -4, -6, -8 }) do
    local at = here + k * 10 / len
    if at > 0 and at < 1 then
      local ok, pos = try(ac, 'trackProgressToWorldCoordinate', at)
      local clear = ok and pos ~= nil and hiddenFromCamera(i, pos)
      for j = 0, sim.carsCount - 1 do
        local other = clear and j ~= i and ac.getCar(j)
        if other and other.position and other.position:distance(pos) < 25 then clear = false end
      end
      if clear then
        if try(physics, 'setCarPosition', i, pos, nil) then try(physics, 'setCarVelocity', i, vec3(0, 0, 0)) end
        return true
      end
    end
  end
  return false
end

local function releaseCar(i, e)
  pcall(physics.setAIThrottleLimit, i, 1)
  pcall(physics.setExtraAIGrip, i, 1.2)
  pcall(physics.disableCarCollisions, i, false)
  if e.baseLevel then pcall(physics.setAILevel, i, e.baseLevel) end
end

local function retireCar(i, e, c)
  if not e.pitting then
    e.pitting, e.pitSince = true, clock()
    releaseCar(i, e)
    try(physics, 'setAIPitStopRequest', i, true)
  end
  -- Park it once it's in its pit box (or, if it never gets there, put it there after two minutes).
  local late = clock() - e.pitSince > 120
  local inPit = field(c, 'isInPit')
  if inPit or late then
    -- Put in its pit box directly (never teleportCarTo: AC's pit spawn routine leaves pit crews round the cars on track).
    if late and not inPit then
      local t = field(c, 'pitTransform')
      local pos = t and field(t, 'position')
      if pos then try(physics, 'setCarPosition', i, pos, field(t, 'look')) end
    end
    try(physics, 'setAINoInput', i, true)
    e.retired = true
  end
end

local function steerToRealResult(step)
  local sim = ac.getSim()
  local session = field(sim, 'raceSessionType')
  if session == nil or not has(ac, 'SessionType') then error('this CSP build can\'t tell the session type: needs a newer CSP', 0) end
  if session ~= ac.SessionType.Race or not sim.isSessionStarted then return end

  local running, leaderLaps = {}, 0
  for i, e in pairs(real.cars) do
    local c = ac.getCar(i)
    if c and not e.retired then
      -- AC retires an AI car that stops moving (a first-lap tangle, say), and preventAIFromRetiring only holds it off
      -- for a while: so it's renewed every tick for every car not yet due to retire as it did in real life.
      if not e.pitting then pcall(physics.preventAIFromRetiring, i) end
      local level = field(c, 'aiLevel')
      if e.baseLevel == nil and level and level > 0 then e.baseLevel = level end
      local run = lapsRun(c, e)
      leaderLaps = math.max(leaderLaps, run)
      if e.retireAt and run >= e.retireAt then
        retireCar(i, e, c)
      elseif not e.pitting and not field(c, 'isRaceFinished') and not (i == 0 and userDriving()) then
        table.insert(running, { i = i, e = e, c = c })
        e.steered = true
        if i ~= 0 then rescueStuck(i, c, step or 0.5) end -- (the user's car is rescued every tick, steered or not)
      elseif e.steered and not e.pitting then
        -- Finished (or you took over your seat): back to its normal pace and collisions.
        releaseCar(i, e)
        e.steered = false
      end
    end
  end
  if #running == 0 then return end

  -- Where each running car should be: finishers by real position, then the cars still to retire (latest first).
  local want = {}
  for _, r in ipairs(running) do table.insert(want, r) end
  table.sort(want, function(a, b)
    local ka = a.e.pos > 0 and a.e.pos or 1000 - (a.e.retireAt or 0)
    local kb = b.e.pos > 0 and b.e.pos or 1000 - (b.e.retireAt or 0)
    return ka < kb
  end)
  local wantRank = {}
  for k, r in ipairs(want) do wantRank[r.i] = k end
  table.sort(running, function(a, b) return (a.c.racePosition or 999) < (b.c.racePosition or 999) end)

  local progress = real.raceLaps > 0 and leaderLaps / real.raceLaps or 0
  local final = progress > 0.75
  real.outOfPlace = 0
  for rank, r in ipairs(running) do
    local err = rank - wantRank[r.i] -- > 0: too far back, < 0: too far forward
    if err ~= 0 then real.outOfPlace = real.outOfPlace + 1 end
    local strength = final and 2 or 1
    local base = r.e.baseLevel or 0.95
    pcall(physics.setAILevel, r.i, math.clamp(base + 0.02 * strength * err, 0.6, 1.0))
    -- Grip only ever goes up (for cars too far back): with less grip than it expects, AC's AI brakes too late and goes
    -- off. Cars too far forward are held back by AI level and, late on, the throttle.
    pcall(physics.setExtraAIGrip, r.i, math.clamp(1.2 + 0.015 * strength * err, 1.2, 1.32))
    if final and err < 0 then
      pcall(physics.setAIThrottleLimit, r.i, math.clamp(0.9 + 0.06 * err, 0.55, 0.9))
    else
      pcall(physics.setAIThrottleLimit, r.i, 1)
    end
    -- Cars out of place may pass through each other late in the race, but only where nobody sees it: never the car
    -- being filmed or its rival, and only more than 250 m from the camera.
    local hidden = r.i ~= watch.target and r.i ~= watch.partner
    if hidden then
      local okCam, cam = try(ac, 'getCameraPosition')
      hidden = okCam and cam ~= nil and r.c.position ~= nil and r.c.position:distance(cam) > 250
    end
    pcall(physics.disableCarCollisions, r.i, final and err ~= 0 and hidden)
  end
end

local function releaseAll()
  for i, e in pairs(real.cars) do
    if e.steered then releaseCar(i, e) e.steered = false end
  end
end

-- ------------------------------------------------------------------ director camera
-- Shot by shot the way real race telecasts are (F1, NASCAR, IndyCar, sports cars):
--  * Trackside cameras stand at fixed places round the circuit, each covering a known stretch of road, and work as
--    a relay: a camera picks the car up as it comes into view and follows it past, and before the car runs out of
--    that stretch the director cuts to the next camera down the road. A camera is only cut to when it can see the
--    car right now and has road ahead of it in view.
--  * Corners are filmed from the outside of the turn (the car comes at the lens, turns and goes away).
--  * Ovals: high cameras on the grandstand roofs show the pack, plus low infield cameras; helicopter "blimp" shots.
--  * Onboards: forward from above the driver, and rear-facing when a rival is right behind.
--  * Helicopter shots from high up beside the track; chase (drone) shots.
--  * The operator keeps the car framed with room ahead of it, the rival (or the pack) in the picture too, and never
--    lets it out of the frame: near the edge the camera turns and zooms out at once.

local function wrap01(p) return p % 1 end
-- How far a is ahead of b in track progress, -0.5..0.5.
local function progressDelta(a, b) local d = (a - b) % 1; if d > 0.5 then d = d - 1 end; return d end

local function trackLen() return field(ac.getSim(), 'trackLengthM') or 0 end
local function hasSpline() return trackLen() > 100 end

-- Track shape, worked out once from the AI racing line: its middle, and whether it's an oval (it only ever turns
-- one way).
local trackShape = nil
local function shape()
  if trackShape then return trackShape end
  local n, sum = 96, vec3()
  local points = {}
  for k = 0, n - 1 do
    points[k] = ac.trackProgressToWorldCoordinate(k / n)
    sum:add(points[k])
  end
  local left, right = 0, 0
  for k = 0, n - 1 do
    local a = points[(k + 1) % n]:sub(points[k], vec3())
    local b = points[(k + 2) % n]:sub(points[(k + 1) % n], vec3())
    local turn = a.x * b.z - a.z * b.x -- sign: which way the line bends here
    if turn > 0.5 then left = left + 1 elseif turn < -0.5 then right = right + 1 end
  end
  local turning = left + right
  trackShape = {
    center = sum:scale(1 / n),
    oval = turning > 8 and math.max(left, right) / turning > 0.93,
  }
  return trackShape
end

-- ---- line of sight
-- Walls and ground (physics), plus the solid scenery that has no collision: buildings, grandstands, bridges, banks.
-- See-through things (catch fences, glass, tree cards) don't count: TV cameras film through and past them.
local solid = { tried = false, ref = nil }
local function solidMeshes()
  if not solid.tried then
    solid.tried = true
    local ok, ref = pcall(function()
      return ac.findNodes('trackRoot:yes'):findMeshes('{ transparent:no & alphaBlend:no & alphaTest:no & castsShadows:yes }')
    end)
    if ok and ref and ref:size() > 0 then solid.ref = ref end
  end
  return solid.ref
end

-- Distance to the track's physics surface along a ray, or -1 for no hit (or a CSP without track raycasts).
local function rayTrack(from, dir, dist)
  local ok, d = try(physics, 'raycastTrack', from, dir, dist)
  return ok and tonumber(d) or -1
end

-- Nothing solid between the camera and a point (the last 2 m before it don't count: the car itself, the kerb).
local function clearView(from, to)
  local dir = to:clone():sub(from)
  local dist = dir:length()
  if dist < 3 then return true end
  dir:scale(1 / dist)
  if rayTrack(from, dir, dist - 2) >= 0 then return false end
  local scene = solidMeshes()
  if scene then
    local ok, d = pcall(function() return scene:raycast(render.createRay(from, dir, dist - 2)) end)
    if ok and d and d >= 0 and d < dist - 2 then return false end
  end
  return true
end

-- The point a camera aims at on a car (about its middle), and on the racing line.
local function carPoint(c) return c.position:clone():addScaled(c.up, 0.7) end
local function trackPoint(progress) return ac.trackProgressToWorldCoordinate(progress):add(vec3(0, 1, 0)) end

-- ---- trackside cameras: placed once per track, a few each frame
local MAX_RANGE = 260 -- metres: furthest a trackside camera films a car from
local spots = { list = {}, count = 0, next = 0, built = false }

-- Placing a camera takes many rays; it runs in a coroutine a couple of milliseconds a frame, so the picture never
-- stutters while the cameras are placed.
local BUILD_BUDGET = 0.002
local builder = { co = nil, inside = false, deadline = 0 }
local function buildYield()
  if builder.inside and clock() > builder.deadline then coroutine.yield() end
end

local function groundAt(pos)
  local probe = pos:clone():add(vec3(0, 80, 0))
  local hit = rayTrack(probe, DOWN, 220)
  if hit < 0 then return nil end
  return probe.y - hit
end

-- How far back and ahead along the track a camera at pos sees the road (metres from its own point of the track).
-- A single short blind spot (a pole, a sign) is stepped over; two in a row end the stretch.
local function coverage(pos, progress)
  local len = trackLen()
  if not clearView(pos, trackPoint(progress)) then return nil end
  local function walk(sign)
    local reach, misses = 0, 0
    for k = 1, 40 do
      local p = trackPoint(wrap01(progress + sign * k * 15 / len))
      if p:distance(pos) > MAX_RANGE then break end
      buildYield()
      if clearView(pos, p) then
        reach, misses = k * 15, 0
      else
        misses = misses + 1
        if misses >= 2 then break end
      end
    end
    return reach
  end
  return walk(-1), walk(1)
end

local function buildSpot()
  if spots.count == 0 then spots.count = math.clamp(math.floor(trackLen() / 110), 16, 80) end
  local k = spots.next
  if k >= spots.count then spots.built = true return end
  spots.next = k + 1
  local len, s = trackLen(), shape()
  local progress = wrap01((k + math.random() * 0.6) / spots.count)
  local base = ac.trackProgressToWorldCoordinate(progress)
  local before = ac.trackProgressToWorldCoordinate(wrap01(progress - 25 / len))
  local after = ac.trackProgressToWorldCoordinate(wrap01(progress + 25 / len))
  local inward = before:clone():add(after):scale(0.5):sub(base) -- towards the inside of a bend
  inward.y = 0
  local bend = inward:length()
  local tangent = after:clone():sub(before)
  tangent.y = 0
  tangent:normalize()
  local side = tangent:cross(UP, vec3()):normalize()
  local okEdges, edges = try(ac, 'getTrackAISplineSides', progress)
  local okWidth, width = pcall(function() return math.max(edges.x, edges.y) end)
  local edge = okEdges and okWidth and tonumber(width) or 7 -- (no usable track width from this CSP: a typical one)

  -- High cameras (grandstand roofs, towers): most of them on ovals, a few elsewhere.
  local high = s.oval and (k % 3 ~= 2) or (k % 4 == 0)
  local sides
  if s.oval then
    -- Roof cameras outside the track, low cameras on the infield.
    local infield = side:dot(s.center:clone():sub(base)) > 0 and 1 or -1
    sides = high and { -infield, infield } or { infield, -infield }
  elseif bend > 1.5 then
    -- A corner: from its outside, looking at the car come in, turn and go away.
    local outside = side:dot(inward) > 0 and -1 or 1
    sides = { outside, -outside }
  else
    local r = math.random() < 0.5 and 1 or -1
    sides = { r, -r }
  end

  for _, sign in ipairs(sides) do
    buildYield()
    -- Low cameras stand well back from the edge (as TV cameras behind the barriers do): right at the edge, a passing
    -- car would need a whip pan.
    local pos = base:clone():addScaled(side, sign * (edge + (high and 14 + math.random() * 18 or 9 + math.random() * 14)))
    local ground = groundAt(pos)
    if ground then
      pos.y = ground + (high and (s.oval and 20 or 12) + math.random() * 12 or 3 + math.random() * 4)
      -- Not under a roof or a bridge.
      if rayTrack(pos, UP, 30) < 0 then
        local back, fwd = coverage(pos, progress)
        if back and back + fwd >= 60 then
          table.insert(spots.list, { pos = pos, progress = progress, back = back, fwd = fwd, high = high, side = sign })
          return
        end
      end
    end
  end
end

-- Metres the car is past a camera's own point of the track (negative: still coming towards it).
local function pastSpot(c, spot) return progressDelta(c.splinePosition, spot.progress) * trackLen() end

-- Does this camera see the car now, with at least `ahead` metres of its road still in view?
local function spotCovers(spot, c, ahead)
  local rel = pastSpot(c, spot)
  return rel >= -spot.back and rel <= spot.fwd - (ahead or 0)
    and spot.pos:distance(carPoint(c)) <= MAX_RANGE and clearView(spot.pos, carPoint(c))
end

-- Fastest a trackside camera pans to follow a car (radians a second, about 65°/s). Faster than that the picture
-- smears into a whip pan; a TV director cuts to the next camera before it comes to that.
local MAX_PAN = 1.15

-- How fast a camera at pos has to turn now to follow a point moving at vel (radians a second).
local function panRate(pos, point, vel)
  local to = point:clone():sub(pos)
  local d = math.max(to:length(), 1)
  to:scale(1 / d)
  return vel:clone():addScaled(to, -vel:dot(to)):length() / d
end

-- Seconds (up to `horizon`) the car can be followed from pos without a pan faster than MAX_PAN. Looks ahead along
-- the racing line at the car's speed, keeping its distance off the line, so a car that turns away in a corner
-- counts as turning away (a straight-line guess would see it run into the camera).
local PAN_STEP = 0.2
local function smoothSeconds(pos, c, horizon)
  local len = trackLen()
  local speed = (c.speedKmh or 0) / 3.6
  if speed < 5 or len < 100 then return horizon end
  local offset = c.position:clone():sub(ac.trackProgressToWorldCoordinate(c.splinePosition)):add(vec3(0, 0.7, 0))
  local lastDir = carPoint(c):sub(pos):normalize()
  for k = 1, math.ceil(horizon / PAN_STEP) do
    local d = ac.trackProgressToWorldCoordinate(wrap01(c.splinePosition + speed * k * PAN_STEP / len))
      :add(offset):sub(pos):normalize()
    if math.acos(math.clamp(d:dot(lastDir), -1, 1)) / PAN_STEP > MAX_PAN then return (k - 1) * PAN_STEP end
    lastDir = d
  end
  return horizon
end

-- The best trackside camera for a car: sees it now and keeps it for a few more seconds without a whip pan. Prefers a
-- camera the car is coming towards, and (opts.high true/false) a high or a low one. Cutting from opts.last, it
-- follows TV editing rules: a camera on the same side of the track (the car keeps moving the same way across the
-- screen), and not one that films it from nearly the same angle (a jump cut).
local function spotFor(c, opts)
  local speed = math.max((c.speedKmh or 0) / 3.6, 10)
  local point = carPoint(c)
  local lastDir = opts.last and point:clone():sub(opts.last.pos):normalize()
  local best, bestScore = nil, -math.huge
  for _, sp in ipairs(spots.list) do
    if sp ~= opts.last then
      local rel = pastSpot(c, sp)
      if rel >= -sp.back and rel <= sp.fwd - 25 and sp.pos:distance(point) <= MAX_RANGE then
        local inStretch = (sp.fwd - rel) / speed -- seconds the car stays in this camera's stretch
        if inStretch >= 2 then
          local bonus = math.random() * 2
          if rel < 0 then bonus = bonus + 1.5 end
          if opts.high ~= nil and sp.high == opts.high then bonus = bonus + 4 end
          if opts.line then
            -- The finish: a camera that sees the car come up to the line and cross it.
            local line = progressDelta(0, sp.progress) * trackLen()
            if line >= -sp.back and line <= sp.fwd - 10 and rel < line then bonus = bonus + 6 end
          end
          if lastDir then
            if sp.side == opts.last.side then bonus = bonus + 2 end
            if point:clone():sub(sp.pos):normalize():dot(lastDir) > 0.9 then bonus = bonus - 4 end
          end
          -- (The look ahead and the ray only for a camera that could still win.)
          if math.min(inStretch, 7) + bonus > bestScore then
            local left = math.min(inStretch, smoothSeconds(sp.pos, c, 7))
            local score = math.min(left, 7) + bonus
            if left >= 2 and score > bestScore and clearView(sp.pos, point) then best, bestScore = sp, score end
          end
        end
      end
    end
  end
  return best
end

-- ---- choosing shots

-- The filmed car is about to take the chequered flag (or just has): the cameras at the line.
local function finishing(c)
  if sessionKind() ~= 'race' or raceLaps() <= 0 then return false end
  if field(c, 'isRaceFinished') then
    local l = lapState[c.index]
    return l ~= nil and l.finishedAt ~= nil and clock() - l.finishedAt < 6
  end
  return (flag.out or (c.lapCount or 0) >= raceLaps() - 1) and (c.splinePosition or 0) > 0.8 and onTrack(c)
end

-- A pit lane camera: on the lane ahead of the car (a car in its pit box faces down the lane), at head height, on the
-- same level as the car (not on the pit building's roof) and with a clear view of it. Nil when there's no such place.
local function pitCameraPos(c)
  local moving = (c.speedKmh or 0) > 8
  local fwd = moving and vec3(c.velocity.x, 0, c.velocity.z) or vec3(c.look.x, 0, c.look.z)
  if fwd:length() < 0.01 then return nil end
  fwd:normalize()
  local side = fwd:cross(UP, vec3()):normalize()
  local point = carPoint(c)
  local first = math.random() < 0.5 and 1 or -1
  local places = moving and { { 45, 9 }, { 32, 8 }, { 60, 10 } } or { { 9, 4 }, { 7, 5 }, { 12, 6 } }
  for _, place in ipairs(places) do
    for _, sign in ipairs({ first, -first }) do
      local pos = c.position:clone():addScaled(fwd, place[1]):addScaled(side, sign * place[2])
      local ground = groundAt(pos)
      if ground and math.abs(ground - c.position.y) < 2.5 then
        pos.y = ground + 1.8
        if clearView(pos, point) then return pos end
      end
    end
  end
  return nil
end

-- Kind of the next shot. Like on TV: mostly trackside cameras; after an onboard, helicopter or chase shot always
-- back to them; at the start of the race wide, high shots of the field; at the finish the cameras at the line; in
-- the pit lane a pit lane camera. Practice and qualifying have more onboards (a qualifying lap from the cockpit).
local function pickKind(c, p)
  local pits = field(c, 'isInPitlane')
  if director.style ~= 'auto' then
    if pits and director.style == 'tv' then return 'pit' end
    return (director.style == 'onboard' and director.noOnboard) and 'chase' or director.style
  end
  if pits then return 'pit' end
  if director.finish then return 'tv' end
  local sim, race = ac.getSim(), sessionKind() == 'race'
  if race and (not sim.isSessionStarted or (director.raceClock or 0) < 25) then return 'tv' end
  if director.lastKind ~= 'tv' or (director.tvRun or 0) < 2 then return 'tv' end
  local behind = p and p.position:clone():sub(c.position):dot(c.look) < -3
    and p.position:distance(c.position) < 30
  local r = math.random()
  if director.noOnboard then return r < 0.75 and 'tv' or r < 0.9 and 'heli' or 'chase' end
  if not race then
    if r < 0.55 then return 'tv' end
    if r < 0.8 then return 'onboard' end
    if r < 0.92 then return 'heli' end
    return 'chase'
  end
  if behind and r < 0.3 then return 'onboard' end
  if r < (p and 0.5 or 0.62) then return 'tv' end
  if r < (p and 0.75 or 0.78) then return 'onboard' end
  if r < 0.9 then return 'heli' end
  return 'chase'
end

local function newShot(c, p)
  local sim, s = ac.getSim(), shape()
  local kind = pickKind(c, p)
  local shot = { kind = kind, age = 0, blocked = 0 }
  if kind == 'pit' then
    local pos = pitCameraPos(c)
    if pos then
      shot.pos, shot.length = pos, (c.speedKmh or 0) > 8 and 14 or 8 + math.random() * 4
      director.pos:set(pos)
      return shot
    end
    -- (No place for a camera in this part of the pit lane: a chase shot down the lane, or from the air.)
    kind = (c.speedKmh or 0) > 8 and 'chase' or 'heli'
    shot.kind, shot.fallback = kind, true
  end
  if kind == 'tv' then
    local start = sessionKind() == 'race' and (not sim.isSessionStarted or (director.raceClock or 0) < 25)
    local high = not director.finish and (start or (s.oval and math.random() < 0.7)) and true or nil
    local last = director.shot
    local spot = spotFor(c, { high = high, last = last and last.spot, line = director.finish })
    if not spot and last and last.spot and not last.lost and director.lastTarget == c.index
      and spotCovers(last.spot, c, 25) and smoothSeconds(last.spot.pos, c, 2) >= 2 then
      -- No other camera has the car, and this one still does: carry on (cutting to the same camera is a jump cut).
      last.age, last.blocked = 0, 0
      return last
    end
    if spot then
      shot.spot, shot.length = spot, 16
      director.pos:set(spot.pos)
      return shot
    end
    -- No trackside camera sees the car here (or they're still being placed): from the air, where nothing is in the way.
    kind = 'heli'
    shot.kind, shot.fallback = kind, true
  end
  if kind == 'onboard' then
    shot.rear = p ~= nil and p.position:clone():sub(c.position):dot(c.look) < -3
    shot.length = 5 + math.random() * 4
  elseif kind == 'heli' then
    shot.sideSign = math.random() < 0.5 and 1 or -1
    shot.length = 8 + math.random() * 6
  else
    shot.length = 6 + math.random() * 4
  end
  return shot
end

local function shotOver(c)
  local s = director.shot
  if not s then return true end
  if director.lastTarget ~= c.index then
    -- Another car to show. When it's close to the last one, no cut: the followed point glides over to it (see
    -- updateDirector). A trackside camera pans over (if it sees the new car well), the helicopter flies over, and the
    -- chase camera moves up to a car just ahead.
    local last = director.lastTarget and ac.getCar(director.lastTarget)
    local gap = last and last.position:distance(c.position) or math.huge
    local glide = s.age > 1.5 and (
      s.kind == 'tv' and gap < 80 and spotCovers(s.spot, c, 20) and smoothSeconds(s.spot.pos, c, 2) >= 2
      or s.kind == 'heli' and gap < 120
      or s.kind == 'chase' and gap < 30 and c.position:clone():sub(last.position):dot(director.heading) > 2)
    if glide then
      director.lastTarget = c.index
      director.panFor = s.kind == 'tv' and 1.8 or 2.2
      director.panTotal = director.panFor
      return false
    end
    return true
  end
  -- Something in the way for longer than a passing pole or tree, or the car can't be held in the picture: cut.
  if s.blocked > 0.35 or s.lost then return true end
  -- The finish: from an onboard, helicopter or chase shot, cut to the cameras at the line.
  if director.finish and director.style == 'auto' and s.kind ~= 'tv' and s.kind ~= 'pit' and not s.fallback
    and s.age > 0.5 then return true end
  if s.kind == 'pit' then
    if not field(c, 'isInPitlane') and s.age > 1.5 then return true end -- out on track: the trackside cameras
    if s.age > 1 and panRate(s.pos, director.track, director.vel) > MAX_PAN * 1.1 then return true end
    local away = c.position:clone():sub(s.pos)
    if (c.speedKmh or 0) > 8 and away:dot(director.vel) > 0 and away:length() > 25 then return true end -- gone by
    return s.age > s.length
  end
  if s.kind == 'tv' then
    -- The car is about to pass so close that following it would be a whip pan: the next camera.
    -- (Cut a moment before it comes to that, as a director watching the car come would.)
    if s.age > 1 and (panRate(s.spot.pos, director.track, director.vel) > MAX_PAN * 1.1
      or (s.age > 2 and smoothSeconds(s.spot.pos, c, 0.4) < 0.4)) then return true end
    local rel = pastSpot(c, s.spot)
    if rel > s.spot.fwd or rel < -s.spot.back - 30 then return true end -- out of this camera's stretch: the next one
    if s.spot.pos:distance(carPoint(c)) > MAX_RANGE + 30 then return true end
    if rel > 30 and s.age > 4 and s.spot.pos:distance(c.position) > 100 then return true end -- gone by, heading away
    return s.age > s.length
  end
  if s.kind == 'onboard' and s.rear and s.age > 2.5 then
    -- Rear-facing: only while the rival is right behind.
    local p = watch.partner ~= nil and ac.getCar(watch.partner)
    if not p or p.position:clone():sub(c.position):dot(c.look) > -2 or p.position:distance(c.position) > 45 then return true end
  end
  return director.style == 'auto' and s.age > s.length
end

-- ---- moving and aiming the camera
-- How the camera stays smooth:
--  * Within a shot nothing ever jumps. The camera follows a point on the car that moves with the car's own velocity
--    (no lag at a steady speed) and is pulled onto the car by a critically damped spring, which filters out bumps
--    and kerbs and starts and stops every correction smoothly. The camera aims straight at that point, so a
--    telephoto shot never falls behind a fast car and needs no catching up.
--  * Framing (room ahead of the car, the rival), zoom, chase distance and helicopter flight are springs as well.
--  * A change of car on the same trackside camera is a smooth pan (the followed point glides across); anything else
--    is a clean cut, as on TV.

-- Onboard: above and behind the driver's head (a T-cam on single-seaters, a roof camera on closed cars), looking
-- forward over the car, or back at the rival. Fixed to the car, as real onboards are: it moves and rolls with it.
-- Its turning and rolling are smoothed a touch, together, so kerbs don't rattle the picture.
local function onboardCamera(c, s, dt, snap)
  local eyes = c.driverEyesPosition
  local top = c.aabbCenter.y + c.aabbSize.y * 0.5
  local y = math.clamp(math.max(eyes.y + 0.3, top + 0.1), 0.5, 2.6)
  local pos = c.bodyTransform:transformPoint(vec3(0, y, eyes.z - (s.rear and 0.7 or 0.45)))
  local look = c.look:clone():scale(s.rear and -1 or 1):addScaled(c.up, -0.07):normalize()
  local t = snap and 1 or 1 - math.exp(-dt * 30)
  director.pos:set(pos)
  director.dir:setLerp(director.dir, look, t):normalize()
  director.up:setLerp(director.up, c.up, t):normalize()
  director.fov = s.rear and 48 or 56
  director.lastGoal = nil -- (the next shot starts its zoom afresh)
end

local function updateDirector(dt)
  local sim = ac.getSim()
  local active = watch.on and director.enabled and not sim.isReplayActive and not userDriving() and watch.target ~= nil
  if not active then
    if director.cam then director.cam:dispose() director.cam = nil end
    return
  end
  if not has(ac, 'grabCamera') then
    -- (said in the Watch tab, not on screen: the race is still filmed, by AC's own cameras)
    director.enabled, director.unsupported = false, true
    return
  end
  local alive = director.cam ~= nil
  if alive then
    local ok, a = pcall(function() return director.cam:active() end)
    alive = not ok or a -- (a CSP without :active(): assume the camera is still held)
  end
  if not alive then
    local cam, err = ac.grabCamera('Historic Roster: watch-only director camera')
    if not cam then
      -- Another script holds the camera: say who, and try again shortly.
      director.error = 'Camera busy: ' .. tostring(err)
      director.retryIn = 3
      return
    end
    director.cam, director.error = cam, nil
    director.shot = nil
  end

  -- Place the trackside cameras, a couple of milliseconds a frame (until they're placed, helicopter shots).
  if hasSpline() and not spots.built then
    builder.co = builder.co or coroutine.create(function() while not spots.built do buildSpot() end end)
    builder.inside, builder.deadline = true, clock() + BUILD_BUDGET
    local ok, err = coroutine.resume(builder.co)
    builder.inside = false
    if not ok then
      spots.built, builder.co = true, nil -- keep the cameras placed so far
      error('Placing trackside cameras: ' .. tostring(err))
    end
    if coroutine.status(builder.co) == 'dead' then builder.co = nil end
  end
  if sim.isSessionStarted then director.raceClock = (director.raceClock or 0) + dt else director.raceClock = 0 end

  local c = ac.getCar(watch.target)
  if not c then return end

  -- The car it's fighting, while it's racing and close. (A little slack once it's in the picture, so it doesn't drop
  -- in and out at the edge of the range.)
  local p = watch.partner ~= nil and ac.getCar(watch.partner) or nil
  if p then
    local range = director.partnerShown == p.index and BATTLE_END_M + 15 or BATTLE_END_M + 5
    if not onTrack(p) or p.position:distance(c.position) > range then p = nil end
  end
  director.partnerShown = p and p.index or nil
  director.finish = finishing(c)

  -- Every new shot is a cut: the camera jumps there instead of panning across the track.
  local snap = false
  if shotOver(c) then
    local shot = newShot(c, p)
    if shot == director.shot then
      -- (The same shot carries on: no cut.)
    elseif director.shot and shot.kind == 'tv' and director.lastKind == 'tv' then
      director.tvRun = (director.tvRun or 0) + 1
    elseif shot.kind == 'tv' then
      director.tvRun = 1
    end
    snap = shot ~= director.shot
    director.shot, director.lastKind, director.lastTarget = shot, shot.kind, c.index
  end
  local s = director.shot
  s.age = s.age + dt
  director.panFor = snap and 0 or math.max(0, director.panFor - dt)

  -- The car's velocity, lightly smoothed (suspension bounce out).
  if snap then director.vel:set(c.velocity) else director.vel:setLerp(director.vel, c.velocity, 1 - math.exp(-dt * 10)) end

  -- Level heading of the car, smoothed in two stages so it eases in and out: chase and helicopter cameras follow it,
  -- so they don't swing with every bump, kerb or twitch of the steering. At speed it's mostly the direction the car
  -- travels, which stays steady when the car slides.
  local flat = vec3(c.look.x, 0, c.look.z)
  local travel = vec3(c.velocity.x, 0, c.velocity.z)
  if travel:length() > 8 and flat:length() > 0.01 then flat:normalize():scale(0.3):addScaled(travel:normalize(), 0.7) end
  if flat:length() > 0.01 then
    flat:normalize()
    -- (A car turned right round, e.g. after a spin: take its new heading at once instead of swinging through it.)
    if snap or director.heading:dot(flat) < -0.3 then
      -- (A chase or helicopter camera would swing right round the car: cut instead.)
      if not snap and (s.kind == 'chase' or s.kind == 'heli') then s.lost = true end
      director.heading:set(flat)
      director.heading1:set(flat)
    else
      local t = 1 - math.exp(-dt * 4)
      director.heading1:setLerp(director.heading1, flat, t):normalize()
      director.heading:setLerp(director.heading, director.heading1, t):normalize()
    end
  end
  local heading = director.heading

  -- The point followed on the car. On a pan from one car to another the spring is softer, so the camera glides over.
  local point = carPoint(c)
  if snap then
    director.track:set(point)
    director.trackErrV:set(0, 0, 0)
  else
    -- (Soft at the start of a pan, firming up gradually: a sudden change of stiffness would jolt the picture.)
    local u = math.clamp(director.panFor / director.panTotal, 0, 1)
    follow(director.track, director.trackErrV, point, director.vel, 9 - 6.4 * u * u * (3 - 2 * u), dt)
  end
  local tracked = director.track

  if s.kind == 'onboard' then
    local ok = pcall(onboardCamera, c, s, dt, snap)
    if not ok then s.lost, director.noOnboard = true, true end -- (no cockpit data from this CSP: no onboards)
  else
    if p and s.noPartner then p = nil end
    -- A camera behind the car (chase) can only frame a rival that is ahead of it (side by side counts).
    if p and s.kind == 'chase' and p.position:clone():sub(c.position):dot(heading) < -4 then p = nil end

    -- 0 alone .. 1 with a rival in the picture: zoom and chase distance ease over with it.
    local rivalGoal = p and 1 or 0
    if snap then
      director.rival, director.rivalV = rivalGoal, 0
    else
      local x, v = spring(director.rival - rivalGoal, director.rivalV, 2.5, dt)
      director.rival, director.rivalV = x + rivalGoal, v
    end
    local rival = math.clamp(director.rival, 0, 1)

    -- Where the camera is.
    if s.kind == 'tv' then
      director.pos:set(s.spot.pos)
    elseif s.kind == 'pit' then
      director.pos:set(s.pos)
    elseif s.kind == 'chase' then
      -- (Moving up to the car ahead, it rises over the one it passes on the way.)
      local lift = director.panFor > 0 and 2.5 * math.sin(math.pi * (1 - director.panFor / director.panTotal)) or 0
      local want = tracked:clone():addScaled(heading, -6.5 - 2.5 * rival):addScaled(UP, 1.4 + 0.7 * rival + lift)
      -- Never under the road (in a dip the road behind the car is higher than the car). The ray starts just above
      -- the camera, so a bridge overhead doesn't count.
      local hit = rayTrack(want:clone():add(vec3(0, 3, 0)), DOWN, 6)
      if hit >= 0 then want.y = math.max(want.y, want.y + 3 - hit + 1.2) end
      if snap then
        director.pos:set(want)
        director.posV:set(0, 0, 0)
      else
        follow(director.pos, director.posV, want, director.vel, 5, dt)
      end
    else -- heli: high up beside the track and a little ahead, flying along with the car
      local side = heading:cross(UP, vec3())
      local want = tracked:clone():addScaled(side, s.sideSign * 60):addScaled(heading, 25):addScaled(UP, 54)
      -- Clear of hills and buildings below it (looked up five times a second; the flight smooths the climb).
      s.groundIn = (s.groundIn or 0) - dt
      if snap or s.groundIn <= 0 then
        s.groundIn, s.ground = 0.2, groundAt(want)
      end
      if s.ground then want.y = math.max(want.y, s.ground + 40) end
      if snap then
        director.pos:set(want)
        director.posV:set(0, 0, 0)
        director.flyVel:set(director.vel)
      else
        -- A helicopter can't brake or turn like a car: its own speed changes slowly, and it swings wide in corners.
        director.flyVel:setLerp(director.flyVel, director.vel, 1 - math.exp(-dt * 1.2))
        follow(director.pos, director.posV, want, director.flyVel, 0.8, dt)
      end
    end

    -- What has to be in the picture: the car and its rival; on high and helicopter shots also the cars round it (the
    -- pack), when they fit. (Which cars are round it is looked up twice a second.)
    local pPoint = p and carPoint(p) or nil
    local pack = {}
    if s.kind == 'heli' or (s.kind == 'tv' and s.spot.high) then
      director.packIn = (director.packIn or 0) - dt
      if snap or director.packIn <= 0 or director.packFor ~= c.index then
        director.packIn, director.packFor, director.packList = 0.5, c.index, {}
        for _, o in ipairs(runningOrder()) do
          if o.index ~= c.index and o.index ~= watch.partner and onTrack(o) and o.position:distance(c.position) < 70 then
            table.insert(director.packList, o.index)
          end
        end
      end
      for _, i in ipairs(director.packList) do
        local o = ac.getCar(i)
        if o and o.position:distance(c.position) < 80 then table.insert(pack, carPoint(o)) end
      end
    end

    -- Aim: at the car, shifted towards its rival, with room ahead of it in the direction it moves across the
    -- picture (as camera operators frame). The offset eases over when a fight starts, ends or changes rival.
    local dist = math.max(director.pos:distance(tracked), 1)
    if snap and s.kind ~= 'chase' then
      -- (A cut: start from the new shot's framing, not the last one's.)
      director.fov = math.clamp(math.deg(2 * math.atan((s.kind == 'heli' and 12 or s.kind == 'pit' and 4 or (s.spot.high and 14 or 4)) / dist)), 3, 45)
    end
    local halfH = dist * math.tan(math.rad(director.fov) / 2)
    local toCar = tracked:clone():sub(director.pos):scale(1 / dist)
    local across = director.vel:clone():addScaled(toCar, -director.vel:dot(toCar))
    local wantOffset = pPoint and pPoint:clone():sub(point):scale(0.5) or vec3()
    if s.kind == 'chase' then
      if not p then wantOffset:addScaled(heading, 5) end
    else
      local a = across:length()
      if a > 0.01 then wantOffset:addScaled(across, math.min(0.45 * halfH, a * 0.4) / a) end
    end
    if snap then
      director.aimOffset:set(wantOffset)
      director.aimOffsetV:set(0, 0, 0)
    else
      springVec(director.aimOffset:sub(wantOffset), director.aimOffsetV, 2.2, dt)
      director.aimOffset:add(wantOffset)
    end
    local aim = tracked:clone():add(director.aimOffset)

    -- Can the camera still see the car (and the one it's fighting)? Checked ten times a second. The chase camera
    -- sits right behind it.
    if s.kind ~= 'chase' then
      s.viewIn = (s.viewIn or 0) - dt
      if s.viewIn <= 0 then
        s.viewIn = 0.1
        local seen = clearView(director.pos, point) and (not pPoint or clearView(director.pos, pPoint))
        if seen then s.blocked = 0 else s.blocked = s.blocked + 0.1 end
      end
    end

    director.dir:set(aim):sub(director.pos):normalize()
    director.up:set(UP)

    -- Zoom: the shot's own framing (how many metres of the scene the picture is high), but never tighter than the
    -- cars need.
    local function needFov(target)
      local to = target:clone():sub(director.pos)
      local d = math.max(to:length(), 1)
      local off = math.deg(math.acos(math.clamp(to:scale(1 / d):dot(director.dir), -1, 1)))
      local size = math.deg(math.atan(3 / d)) -- half a car length either side
      return 2 * (off + size) * 1.3          -- with a margin round the edge of the picture
    end
    local wantFov
    if s.kind == 'chase' then
      wantFov = 52 + 6 * rival
    else
      local frameH = s.kind == 'heli' and 24 or s.kind == 'pit' and 6 or (s.spot.high and 28 or 8)
      wantFov = math.clamp(math.deg(2 * math.atan((frameH / 2 + 2 * rival) / dist)), 3, 45)
      for _, pt in ipairs(pack) do wantFov = math.max(wantFov, math.min(needFov(pt), 40)) end
    end
    local need = needFov(tracked)
    if pPoint then
      local needP = needFov(pPoint)
      if needP > need then need = need + (needP - need) * rival end -- (the rival comes into the picture gradually)
    end
    local goal = math.clamp(math.max(wantFov, math.min(need, 70)), 3, 70)

    -- Smoothed in log scale (zooming 20° to 40° looks as big a change as 5° to 10°). The shot's own change of framing
    -- (a car coming closer) is followed without lag; widening (a car nearing the edge, a rival coming into the
    -- picture) is quick, zooming in slow so the picture doesn't pump in and out, and both start and stop smoothly.
    local logGoal = math.log(goal)
    if snap or not director.lastGoal then
      director.logFov, director.logFovV, director.goalRate = logGoal, 0, 0
    else
      local rate = math.clamp((logGoal - director.lastGoal) / math.max(dt, 0.001), -2.5, 2.5)
      director.goalRate = math.lerp(director.goalRate, rate, 1 - math.exp(-dt * 4))
      local x = director.logFov + director.goalRate * dt - logGoal
      local omega = x < 0 and (need > director.fov * 1.05 and 9 or 4) or 1.3
      x, director.logFovV = spring(x, director.logFovV, omega, dt)
      director.logFov = logGoal + x
    end
    director.lastGoal = logGoal
    director.fov = math.clamp(math.exp(director.logFov), 3, 70)

    -- Last resort, if the car still gets right to the edge of the picture: turn just enough to hold it in. (Not while
    -- panning over to another car: that one comes into the picture as the camera gets there.)
    if director.panFor <= 0 then
      local toC = point:clone():sub(director.pos)
      local dC = math.max(toC:length(), 1)
      toC:scale(1 / dC)
      local limit = math.rad(director.fov) * 0.5 * 0.92 - math.atan(3 / dC)
      local off = math.acos(math.clamp(director.dir:dot(toC), -1, 1))
      if limit > 0.005 and off > limit then
        director.dir:setLerp(director.dir, toC, (off - limit) / off):normalize()
      end
    end

    if need > 70 then
      -- Both cars can't be held in one picture: keep the filmed car only. If even that one can't be held, cut.
      if p then s.noPartner = true elseif s.age > 0.3 then s.lost = true end
    end
  end

  local cam = director.cam
  cam.transform.position:set(director.pos)
  cam.transform.look:set(director.dir)
  cam.transform.up:set(director.up)
  pcall(function() cam:normalize() end)
  cam.fov = director.fov
end

-- ------------------------------------------------------------------ the Safety Car never races
-- The program can put AdvancedAI's Safety Car at the back of the grid. With AdvancedAI switched on, AdvancedAI holds
-- it in its pit box (with AC's own start delay) and sends it out only to neutralise the race: then the Safety Car is
-- left entirely to AdvancedAI, and anything this app did to it is undone. With AdvancedAI switched off, Assetto Corsa
-- would race it like any other AI car: then, in every offline session, driven or watched, it is sent to its pit box
-- and parked there (no AI input).
-- Whether AdvancedAI is on comes from its journal (Documents\Assetto Corsa\logs\AdvancedIA.log), where it writes a
-- summary with its state ("garde disabled", "guard ready" ...) when a session starts and every minute. When that
-- can't be told (journal switched off, a format this app doesn't know), the Safety Car is parked only once it's
-- plainly racing: driving on track, out of the pit lane, for 10 s, without ever having been in the pits.
local safetyGuard = { timer = 0, session = -1, startedAt = nil, seenInPits = {}, racing = {}, parked = {}, held = {}, noted = false }

-- AdvancedAI's state words: running the race, or not running it.
local aiaOn = { ready = true, paused = true, finished = true, init = true, dt = true, replay = true }
local aiaOff = { disabled = true, nosections = true, spline = true, panic = true }
local aiaState = { value = nil, readAt = -1e9 }

-- 'on', 'off', or nil when it can't be told: from AdvancedAI's latest summary in this launch, if under 150 s old.
local function advancedAiState()
  local now = clock()
  if now - aiaState.readAt < (aiaState.value and 20 or 5) then return aiaState.value end
  aiaState.readAt = now
  local value = nil
  pcall(function()
    local f = io.open(ac.getFolder(ac.FolderID.Logs) .. '/AdvancedIA.log', 'rb')
    if not f then return end
    local size = f:seek('end') or 0
    f:seek('set', math.max(0, size - 32768))
    local text = f:read('*a') or ''
    f:close()
    local stamp, state, tabs
    for line in text:gmatch('[^\r\n]+') do
      if line:sub(1, 3) == '===' then
        stamp, state, tabs = nil, nil, nil -- a new launch: only its own summaries count
      else
        -- The sections switched on this session ("onglets: general,blue,... ; ...").
        local list = line:match('onglets: ([%a,]+)') or line:match('tabs: ([%a,]+)')
        if list then
          tabs = {}
          for t in list:gmatch('%a+') do tabs[t] = true end
        end
        local s = line:match('[%s,|]garde (%a+)') or line:match('[%s,|]guard (%a+)')
        local y, mo, d, h, mi, se = line:match('^(%d+)%-(%d+)%-(%d+) (%d+):(%d+):(%d+)')
        if s and y then
          stamp = os.time({ year = tonumber(y), month = tonumber(mo), day = tonumber(d),
                            hour = tonumber(h), min = tonumber(mi), sec = tonumber(se) })
          state = s
        end
      end
    end
    if state and stamp and math.abs(os.time() - stamp) <= 150 then
      value = aiaOff[state] and 'off' or aiaOn[state] and 'on' or nil
    end
    aiaState.tabs = tabs
  end)
  aiaState.value = value
  return value
end

-- AdvancedAI runs the Safety Car only when it's on with its Accidents section (where the Safety Car lives). Set up
-- to drive the cars only, it leaves the Safety Car alone, so this app has to keep it in its pit box. 'on', 'off' or
-- nil (can't be told).
local function advancedAiSafetyCar()
  local state = advancedAiState()
  if state == 'on' and aiaState.tabs and not aiaState.tabs.accidents then return 'off' end
  return state
end

-- Parks it: held where it stands at once (no AI input, so it never pulls away), then, once the session has been
-- running 5 s and the field has left it behind, put straight into its pit box (physics.setCarPosition to the car's
-- pit position). Never with physics.teleportCarTo: that runs AC's own "spawn in the pits" routine, and run during a
-- session it leaves the pit crews standing round the cars out on track. A CSP without setCarPosition or the pit
-- position lets it drive into the pits instead, and holds it there.
local function parkSafetyCar(i, c, why)
  local p = safetyGuard.parked[i]
  if not p then
    p = { since = clock() }
    safetyGuard.parked[i] = p
    if not safetyGuard.noted then
      safetyGuard.noted = true
      try(ac, 'setMessage', 'Historic Roster', 'Safety Car kept in its pit box: ' .. why)
    end
  end
  local inPits = field(c, 'isInPitlane') or field(c, 'isInPit')
  if not p.moved and not p.driving and not inPits and safetyGuard.startedAt and clock() - safetyGuard.startedAt > 5 then
    local t = field(c, 'pitTransform')
    local pos = t and field(t, 'position')
    if pos and try(physics, 'setCarPosition', i, pos, field(t, 'look')) then
      p.moved = true
      if vec3 then try(physics, 'setCarVelocity', i, vec3(0, 0, 0)) end
    else
      p.driving = true
      if try(physics, 'setAINoInput', i, false) then safetyGuard.held[i] = nil end
      try(physics, 'setAIPitStopRequest', i, true)
    end
  end
  -- Driving to the pits: held once it's in its pit box. Otherwise held from the first moment.
  if p.driving and not field(c, 'isInPit') then return end
  if try(physics, 'setAINoInput', i, true) then safetyGuard.held[i] = true end
end

-- AdvancedAI is running the Safety Car: hand it back exactly as AdvancedAI expects it.
local function releaseSafetyCar(i)
  if safetyGuard.held[i] then try(physics, 'setAINoInput', i, false) end
  if safetyGuard.parked[i] and safetyGuard.parked[i].driving then try(physics, 'setAIPitStopRequest', i, false) end
  safetyGuard.held[i], safetyGuard.parked[i] = nil, nil
end

local function guardSafetyCar(dt)
  local sim = ac.getSim()
  if sim.isOnlineRace or sim.isReplayActive then return end
  safetyGuard.timer = safetyGuard.timer + dt
  if safetyGuard.timer < 0.5 then return end
  local step = safetyGuard.timer
  safetyGuard.timer = 0
  if not raceCars.loaded then loadRaceCars() end
  if next(raceCars.safety) == nil then return end

  -- A new session, or a restarted race (back on the grid after it had started): watch the Safety Car afresh. A car
  -- this app holds (no AI input) stays held, and is parked again from wherever the restart put it.
  local restart = safetyGuard.startedAt ~= nil and not sim.isSessionStarted
  if sim.currentSessionIndex ~= safetyGuard.session or restart then
    safetyGuard.session, safetyGuard.startedAt, safetyGuard.seenInPits, safetyGuard.racing, safetyGuard.parked =
      sim.currentSessionIndex, nil, {}, {}, {}
  end
  if sim.isSessionStarted and not safetyGuard.startedAt then safetyGuard.startedAt = clock() end

  local state = advancedAiSafetyCar()
  local race = sessionKind() == 'race'
  for i in pairs(raceCars.safety) do
    local c = ac.getCar(i)
    if c then
      if state == 'on' then
        -- AdvancedAI runs it (and can send it out): hands off, and undo a park from before it was known to be on.
        if safetyGuard.parked[i] or safetyGuard.held[i] then releaseSafetyCar(i) end
      elseif state == 'off' then
        parkSafetyCar(i, c, 'AdvancedAI isn\'t running it, so it would race like any other car.')
      elseif safetyGuard.parked[i] or safetyGuard.held[i] then
        parkSafetyCar(i, c, 'it was racing like any other car.')
      else
        -- AdvancedAI's state unknown: park it only once it's plainly racing.
        local inPits = field(c, 'isInPitlane') or field(c, 'isInPit')
        if inPits then safetyGuard.seenInPits[i] = true end
        local moving = sim.isSessionStarted and not inPits and (c.speedKmh or 0) > (race and 30 or 20)
        safetyGuard.racing[i] = moving and (safetyGuard.racing[i] or 0) + step or 0
        local loose
        if race then
          loose = not safetyGuard.seenInPits[i] and safetyGuard.racing[i] >= 10
        else
          loose = safetyGuard.racing[i] >= 3
        end
        if loose then parkSafetyCar(i, c, 'it was racing like any other car.') end
      end
    end
  end
end

function script.update(dt)
  -- Every offline session, watched or not.
  pcall(guardSafetyCar, dt)

  if not watch.checked then
    watch.checked = true
    -- Watch-only races only: never in online races, never without the flag the program writes.
    watch.on = not ac.getSim().isOnlineRace and readWatchFlag()
  end
  if not watch.on then return end
  local sim = ac.getSim()

  -- Every frame: the camera.
  if sim.isReplayActive then
    if director.cam then director.cam:dispose() director.cam = nil end
  elseif (director.retryIn or 0) > 0 then
    director.retryIn = director.retryIn - dt
  else
    local ok, err = pcall(updateDirector, dt)
    if not ok then
      -- Shown on screen; the director tries again in a few seconds instead of giving up.
      director.error = tostring(err)
      director.retryIn = 3
      director.shot = nil
      if director.cam then director.cam:dispose() director.cam = nil end
    end
  end

  if sim.isReplayActive then return end
  updateIdealLine(dt)

  watch.timer = watch.timer + dt
  if watch.timer < 0.5 then return end
  local step = watch.timer
  watch.timer = 0

  -- A new session (or a restart): engage the autopilot again.
  if sim.currentSessionIndex ~= watch.session then
    watch.session = sim.currentSessionIndex
    watch.engaged, watch.userOff = false, false
    for k in pairs(stuckCars) do stuckCars[k] = nil end
    -- Each session is filmed afresh: laps, the flag, who has been shown; a race starts on your seat again.
    watch.fresh, watch.lastShown, watch.targetHeld = true, {}, 99
    for k in pairs(lapState) do lapState[k] = nil end
    flag.out = false
    director.shot, director.raceClock, director.finish = nil, 0, false
    ideal.tries = 0 -- AC ignores the ideal-line command until a session runs: try again in each one
    -- A restart: un-park retired cars and start the real result over.
    for i, e in pairs(real.cars) do
      if e.pitting or e.retired then pcall(physics.setAINoInput, i, false) pcall(physics.setAIPitStopRequest, i, false) end
      if e.steered then releaseCar(i, e) end
      e.pitting, e.retired, e.steered, e.crossed = false, false, false, false
    end
  end

  local car = ac.getCar(0)
  if car and watch.wantAi and not watch.userOff then
    if car.isAIControlled then
      watch.engaged = true
    elseif watch.engaged then
      -- Switched off with AC's own autopilot key: the user is driving now.
      watch.userOff = true
      try(ac, 'setMessage', 'Historic Roster', 'Autopilot off: you are driving. "Let AI drive" in the Historic Roster window hands the car back.')
    else
      setAutopilot(true)
    end
  end

  -- CAR_0's real driver: Content Manager, starting the race, puts its own player's name and flag on CAR_0.
  if not watch.nameSet then
    watch.nameSet = true
    local cfg = ourRaceConfig()
    if cfg then
      local okN, name = pcall(function() return cfg:get('HISTORIC_ROSTER', 'CAR0_NAME', '') end)
      local okC, code = pcall(function() return cfg:get('HISTORIC_ROSTER', 'CAR0_NATION', '') end)
      if okN and type(name) == 'string' and name ~= '' and ac.getDriverName(0) ~= name then
        try(physics, 'setDriverName', name, okC and type(code) == 'string' and #code == 3 and code or nil)
      end
    end
  end

  -- The user's car (CAR_0) never pits while the AI drives it: AC opens its pit menu for that car and waits for you.
  -- Kept fuelled instead, so it never has to. (Driving it yourself, you pit as usual.)
  if car and not userDriving() and car.isAIControlled then
    try(physics, 'setAIPitStopRequest', 0, false)
    -- AC wears the user's engine (over-revving on the grid and on downshifts) but never an AI car's: kept as new, so
    -- the real driver's car isn't the only one on the grid with a tired engine.
    local life = field(car, 'engineLifeLeft')
    if life and life < 1000 then try(physics, 'setCarEngineLife', 0, 1000) end
    -- Stuck after a spin or a tangle: nothing else recovers the user's car, and AC never retires it.
    rescueStuck(0, car, step)
    local fuel, maxFuel = field(car, 'fuel'), field(car, 'maxFuel')
    if fuel and maxFuel and maxFuel > 0 and fuel < maxFuel * 0.3 then try(physics, 'setCarFuel', 0, maxFuel) end
  end

  -- A watched race has no driver: none of AC's HUD for the user's car (fuel, damage, proximity radar, wrong way, the
  -- quick pit menu, the virtual mirror) and none of its messages to the user (penalties, cuts, slow-downs).
  if not watch.spectating then
    watch.spectating = true
    try(ac, 'disableExtraHUDElements', { 'fuel', 'damage', 'proximity', 'wrongWay', 'quickPitsMenu', 'virtualMirror' }, true)
    local ok, block = try(ac, 'blockSystemMessages',
      'PENALTY|SLOW DOWN|CUT DETECTED|LAP WILL NOT COUNT|DRIVE.?THROUGH|STOP.?&.?GO|STOP AND GO|JUMP.?START|DISQUALIFIED')
    if ok then watch.messageBlock = block end -- (kept: the block lasts while the disposable lives)
  end

  -- Every car with its headlights on, as on TV (CSP scene tweak; the object is kept so the setting stays). Another
  -- script may hold the scene tweaks: then try again on the next tick.
  if not watch.sceneTweaks then
    local ok, tweaks = pcall(ac.configureSceneTweaks)
    if ok and tweaks then
      watch.sceneTweaks = tweaks
      pcall(function() tweaks.forceHeadlights = ac.SceneTweakFlag.ForceOn end)
    end
  end

  chooseTarget(step)

  -- Steer the race to the real result.
  if not real.loaded then loadRealResult() end
  if real.available then
    if real.enabled then
      local ok, err = pcall(steerToRealResult, step)
      real.status = not ok and tostring(err) or nil
    elseif real.wasEnabled then
      releaseAll()
    end
    real.wasEnabled = real.enabled
  end
end

-- Where the session is: "Race, lap 12 / 50", "Qualifying, 8:31 left" ... (sep: between the session and the rest)
local function sessionLine(order, sep)
  local sim, kind = ac.getSim(), sessionKind()
  if kind == 'race' then
    local n, lead = raceLaps(), order[1]
    if flag.out then return 'Race' .. sep .. 'Chequered flag' end
    if not sim.isSessionStarted then return n > 0 and string.format('Race%s%d laps', sep, n) or 'Race' end
    if n > 0 and lead then
      local lap = math.min(n, (lead.lapCount or 0) + 1)
      return lap == n and 'Race' .. sep .. 'Final lap' or string.format('Race%sLap %d / %d', sep, lap, n)
    end
    return 'Race'
  end
  local name = kind == 'qualify' and 'Qualifying' or 'Practice'
  local left = tonumber(field(sim, 'sessionTimeLeft'))
  if not sim.isSessionStarted or not left then return name end
  if left > 0 then
    return string.format('%s%s%d:%02d left', name, sep, math.floor(left / 60000), math.floor(left / 1000) % 60)
  end
  return name .. sep .. 'Chequered flag'
end

-- What the filmed car is doing, for the caption: its lap in practice and qualifying, its finish in the race. Text and
-- colour, or nil.
local function carStatus(order, c)
  local l = lapState[c.index]
  local gold, white, grey = rgbm(1, 0.82, 0.35, 1), rgbm(1, 1, 1, 0.85), rgbm(1, 1, 1, 0.6)
  local pits = field(c, 'isInPitlane')
  if sessionKind() == 'race' then
    if field(c, 'isRaceFinished') and l and l.finishedAt and clock() - l.finishedAt < 10 then
      local cp, _, cname = classPosition(order, c.index)
      if c.racePosition == 1 then return 'Wins the race', gold end
      if cp == 1 then return 'Wins ' .. cname, gold end
      return cp and string.format('Finishes %s P%d', cname, cp) or string.format('Finishes P%d', c.racePosition or 0), gold
    end
    if pits then return 'In the pit lane', grey end
    return nil
  end
  if pits then return (c.speedKmh or 0) > 3 and 'In the pit lane' or 'In the pits', grey end
  -- (The lap just completed: a few seconds of its time and the place it's worth.)
  local prev, best = tonumber(field(c, 'previousLapTimeMs')) or 0, tonumber(field(c, 'bestLapTimeMs')) or 0
  if l and l.donePush and l.doneAt and clock() - l.doneAt < 6 and prev > 0 then
    local text = 'Lap ' .. lapTimeText(prev)
    if prev == best then text = text .. ((c.racePosition == 1) and '   fastest' or '   personal best') end
    return text .. string.format('   P%d', c.racePosition or 0), gold
  end
  if l and l.pushing then
    local text = 'Flying lap   ' .. (lapTimeText(field(c, 'lapTimeMs'), true) or '')
    local perf = tonumber(field(c, 'performanceMeter'))
    if perf and perf ~= 0 and best > 0 then -- (0: this CSP keeps no delta for the car)
      return text .. string.format('   %+.2f', perf), perf < 0 and rgbm(0.45, 1, 0.5, 1) or white
    end
    return text, white
  end
  if onTrack(c) then return (l and (c.lapCount or 0) <= l.pitLap) and 'Out lap' or 'Slow lap', grey end
  return nil
end

local function tabWatch()
  local sim = ac.getSim()
  local car = ac.getCar(0)
  ui.textColored('AI race: every car is driven by the AI.', colors.head)
  ui.text('Autopilot on your seat: ' .. ((car and car.isAIControlled) and 'on' or 'off'))
  ui.sameLine()
  if car and car.isAIControlled then
    if ui.button('Drive yourself') then watch.wantAi = false setAutopilot(false) end
  else
    if ui.button('Let AI drive') then watch.wantAi, watch.userOff, watch.engaged = true, false, false setAutopilot(true) end
  end
  if watch.noAutopilot then
    ui.textColored("This CSP build can't switch the autopilot from an app: use Assetto Corsa's own autopilot key.", colors.head)
  end
  local idealShown = field(sim, 'idealLineShown')
  if idealShown ~= nil then
    ui.textDisabled('Ideal line: ' .. (idealShown and 'on' or 'off') .. (userDriving() and '' or ' (turned off while watching)'))
  end

  if real.available then
    if ui.checkbox('Finish as in real life', real.enabled) then real.enabled = not real.enabled end
    if ui.itemHovered() then
      ui.setTooltip('Steers every car to its real finishing position, and retires cars into the pits when they really retired.')
    end
    ui.sameLine()
    if real.status then ui.textColored(real.status, colors.bad)
    elseif real.enabled then
      ui.textDisabled(real.outOfPlace == 0 and 'every car is in its real place' or (real.outOfPlace .. ' car(s) still out of their real place'))
    end
    -- AdvancedAI drives the AI cars in place of Kunos's stock AI; the real result is steered on top of it.
    local aia = advancedAiState()
    if aia == 'on' then
      ui.textDisabled('AdvancedAI is driving the AI cars; "Finish as in real life" steers their pace on top of it.')
    elseif aia == 'off' then
      ui.textColored('AdvancedAI is switched off in its own window: Kunos\'s stock AI is driving the cars.', colors.head)
    end
  end
  ui.separator()

  ui.textColored(sessionLine(runningOrder(), ', '), colors.head)
  ui.text('Follow:')
  ui.sameLine()
  if ui.radioButton('Whole grid', watch.follow == 'grid') then watch.follow, watch.targetHeld = 'grid', 99 end
  if ui.itemHovered() then
    ui.setTooltip('Race: rotates through every car on the grid: battles, the leader, and whoever has gone longest without being shown.\n' ..
      'Practice and qualifying: the cars on flying laps, each followed to the line.')
  end
  ui.sameLine()
  if ui.radioButton('Leader', watch.follow == 'leader') then watch.follow = 'leader' end
  if ui.itemHovered() then ui.setTooltip('Race: the leader. Practice and qualifying: the fastest car, while it is out on track.') end
  ui.sameLine()
  if ui.radioButton('Closest battle', watch.follow == 'battle') then watch.follow, watch.targetHeld = 'battle', 99 end
  if ui.itemHovered() then ui.setTooltip('Race only (practice and qualifying have no battles: the flying laps are shown).') end
  ui.sameLine()
  if ui.radioButton('Pick a car', watch.follow == 'manual') then watch.follow = 'manual' watch.manualTarget = watch.manualTarget or watch.target end

  if director.unsupported then
    ui.textColored('This CSP build has no scripted camera, so the director camera is off.', colors.head)
  elseif ui.checkbox('Director camera (TV shots of the car you follow)', director.enabled) then
    director.enabled = not director.enabled
    director.error = nil
  end
  if director.error then ui.textColored(director.error, colors.bad) end
  if director.enabled then
    ui.text('Shots:')
    for _, s in ipairs({ { 'auto', 'Auto' }, { 'tv', 'Trackside' }, { 'onboard', 'Onboard' }, { 'heli', 'Helicopter' }, { 'chase', 'Chase' } }) do
      ui.sameLine()
      if ui.radioButton(s[2] .. '##shot', director.style == s[1]) then director.style = s[1] director.shot = nil end
    end
    ui.sameLine()
    if ui.button('Next shot') then director.shot = nil end
    ui.sameLine()
    if ui.button('Next car') then watch.follow, watch.targetHeld, watch.skip = 'grid', 99, true end
    if hasSpline() then
      ui.textDisabled(spots.built and (#spots.list .. ' trackside cameras placed round the track')
        or ('Placing trackside cameras... (' .. #spots.list .. ' so far)'))
    end
  else
    ui.textDisabled("Assetto Corsa's own cameras (these follow your seat's car only).")
  end
  ui.separator()

  -- Running order: click a driver to watch them.
  ui.childWindow('watchOrder', vec2(ui.availableSpaceX(), ui.availableSpaceY()), false, function()
    local order = runningOrder()
    local times = sessionKind() ~= 'race'
    for _, c in ipairs(order) do
      local cp, _, cname = classPosition(order, c.index)
      -- (Practice and qualifying: each car's best lap.)
      local best = times and (lapTimeText(field(c, 'bestLapTimeMs')) or 'no time') or nil
      local name = (ac.getDriverName(c.index) or '?') .. (best and ('   ' .. best) or '')
      local label = cp and string.format('P%-3d %s P%d  %s##w%d', c.racePosition or 0, cname, cp, name, c.index)
        or string.format('P%-3d %s##w%d', c.racePosition or 0, name, c.index)
      if ui.selectable(label, watch.target == c.index) then
        watch.follow, watch.manualTarget = 'manual', c.index
      end
    end
  end)
end

-- Class standings, in a multi-class race: every class in its running order, with the overall place and laps down.
local function tabClasses()
  local order = runningOrder()
  ui.textDisabled(watch.on and 'Positions within each class. Click a driver to watch them.' or 'Positions within each class.')
  ui.childWindow('classOrder', vec2(ui.availableSpaceX(), ui.availableSpaceY()), false, function()
    for _, name in ipairs(classGroups()) do
      local g = classOrder(order, name)
      if #g > 0 then
        ui.textColored(string.format('%s  (%d car%s)', name == '' and 'Other' or name, #g, #g == 1 and '' or 's'), colors.head)
        for k, c in ipairs(g) do
          local down = k > 1 and math.floor(distanceRun(g[1]) - distanceRun(c)) or 0
          local label = string.format('P%-3d %s   (P%d overall%s)##cl%d', k, ac.getDriverName(c.index) or '?', c.racePosition or 0,
            down >= 1 and string.format(', +%d lap%s', down, down == 1 and '' or 's') or '', c.index)
          local mine = c.index == 0 and not watch.on
          if ui.selectable(label, (watch.on and watch.target == c.index) or mine) and watch.on then
            watch.follow, watch.manualTarget = 'manual', c.index
          end
        end
        ui.separator()
      end
    end
  end)
end

-- While you drive a multi-class race: your class, class position and overall place, top centre.
local function drawClassBox()
  local c = ac.getCar(0)
  if not c then return end
  local pos, n, name = classPosition(runningOrder(), 0)
  if not pos then return end
  local text = string.format('%s  P%d / %d   ·   P%d overall', name, pos, n, c.racePosition or 0)
  local size = ac.getUI().windowSize
  local w = 24 + #text * 9
  local x, y = (size.x - w) / 2, 12
  ui.drawRectFilled(vec2(x, y), vec2(x + w, y + 32), rgbm(0, 0, 0, 0.55), 4)
  drawText(text, 17, vec2(x + 12, y + 6), rgbm(1, 1, 1, 1))
end

-- On-screen caption in watch-only races: who is being filmed (manifest [UI_CALLBACKS] IN_GAME). In a multi-class
-- race you drive, your class position instead.
function script.watchOverlay()
  if ac.getSim().isReplayActive then return end
  if not watch.on then
    if settings.classBox and multiClass() then drawClassBox() end
    return
  end
  local size = ac.getUI().windowSize
  local lines = {}
  local c = watch.target ~= nil and ac.getCar(watch.target) or nil
  if c and director.enabled and not userDriving() then
    local okNumber, number = try(ac, 'getDriverNumber', c.index)
    if not okNumber then number = nil end
    local order = runningOrder()
    local cp, _, cname = classPosition(order, c.index)
    table.insert(lines, { sessionLine(order, '   ·   '), 14, rgbm(1, 1, 1, 0.6) })
    table.insert(lines, { string.format('P%d  %s%s%s', c.racePosition or 0, (tonumber(number) or 0) > 0 and ('#' .. tostring(number) .. '  ') or '',
      ac.getDriverName(c.index) or '?', cp and string.format('   ·   %s P%d', cname, cp) or ''), 22, rgbm(1, 1, 1, 1) })
    table.insert(lines, { ac.getCarName(c.index) or '', 15, rgbm(1, 1, 1, 0.75) })
    local p = watch.partner ~= nil and ac.getCar(watch.partner) or nil
    if p and ac.getSim().isSessionStarted then -- (side by side on the grid isn't a battle yet)
      local front = math.min(c.racePosition or 99, p.racePosition or 99)
      local pp = classPosition(order, p.index)
      local place = cp and pp and string.format('%s P%d', cname, math.min(cp, pp)) or ('P' .. front)
      table.insert(lines, { string.format('Battle for %s with %s', place, ac.getDriverName(p.index) or '?'), 15, rgbm(1, 0.82, 0.35, 1) })
    end
    local status, color = carStatus(order, c)
    if status then table.insert(lines, { status, 15, color }) end
  end
  if director.error then table.insert(lines, { 'Director camera: ' .. director.error, 14, rgbm(1, 0.5, 0.45, 1) }) end
  if #lines == 0 then return end
  local height = 0
  for _, l in ipairs(lines) do height = height + l[2] + 6 end
  local x, y = 24, size.y - 26 - height -- (bottom left, growing upwards with the lines)
  ui.drawRectFilled(vec2(x - 12, y - 8), vec2(x + 520, y + height + 2), rgbm(0, 0, 0, 0.55), 4)
  for _, l in ipairs(lines) do
    drawText(l[1], l[2], vec2(x, y), l[3])
    y = y + l[2] + 6
  end
end

-- ------------------------------------------------------------------ window

function script.windowMain(dt)
  ui.tabBar('historicRosterTabs', function()
    if watch.on then ui.tabItem('Watch', tabWatch) end
    if multiClass() then ui.tabItem('Classes', tabClasses) end
    ui.tabItem('Races', tabRaces)
    ui.tabItem('Grid', tabGrid)
    ui.tabItem('Get content', tabContent)
    ui.tabItem('Admin', tabAdmin)
    ui.tabItem('Settings', tabSettings)
  end)

  -- Log of server replies and RosterTool output.
  if #state.log > 0 then
    ui.separator()
    ui.childWindow('log', vec2(ui.availableSpaceX(), 110), true, function()
      for i = math.max(1, #state.log - 60), #state.log do ui.textWrapped(state.log[i]) end
      if ui.setScrollHereY then ui.setScrollHereY(1) end
    end)
  end
end
