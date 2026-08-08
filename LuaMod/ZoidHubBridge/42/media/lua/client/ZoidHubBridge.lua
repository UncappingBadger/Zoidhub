-- ZoidHub Bridge: writes the local player's world position to
-- Zomboid/Lua/ZoidHubBridge/position.json every ~1 second so the ZoidHub companion
-- app (a separate desktop app, not part of this mod) can show a live map marker.
-- Reads only the local player's own position - nothing else, nothing sent over the network.

local tickCounter = 0
local WRITE_INTERVAL_TICKS = 30 -- roughly once a second at 30 ticks/sec

local function currentTimestampMs()
    local ok, seconds = pcall(getTimestamp)
    if ok and seconds then
        return math.floor(seconds * 1000)
    end
    return 0
end

local function writePosition()
    local writer = getFileWriter("ZoidHubBridge/position.json", true, false)
    local player = getPlayer()

    if not player then
        writer:write('{"inGame":false}')
        writer:close()
        return
    end

    local json = string.format(
        '{"name":"%s","x":%.2f,"y":%.2f,"layer":%d,"inGame":true,"updatedAtUnixMs":%d}',
        tostring(player:getUsername() or "player"),
        player:getX(),
        player:getY(),
        math.floor(player:getZ() + 0.5),
        currentTimestampMs()
    )
    writer:write(json)
    writer:close()
end

local function onTick()
    tickCounter = tickCounter + 1
    if tickCounter < WRITE_INTERVAL_TICKS then
        return
    end
    tickCounter = 0

    local ok, err = pcall(writePosition)
    if not ok then
        print("ZoidHubBridge error: " .. tostring(err))
    end
end

Events.OnTick.Add(onTick)
