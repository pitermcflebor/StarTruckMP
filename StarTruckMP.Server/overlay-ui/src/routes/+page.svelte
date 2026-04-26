<script lang="ts">
    import {type GameMessage, onGameMessage, sendToGame} from "$lib/gameEvents";
    
    let lastMessage = $state<GameMessage>({} as GameMessage);
    
    onGameMessage((message => {
        lastMessage = message;
    }));
    
    sendToGame("overlayLoaded", { timestamp: Date.now() });
    
    function testBtn() {
        sendToGame("testButtonClicked", { timestamp: Date.now() });
    }
</script>

<h1>StarTruckMP overlay loaded</h1>
<h2>Last message: {lastMessage.type} | {lastMessage.source}</h2>
<code>
    {JSON.stringify(lastMessage.payload, null, 2)}
</code>

<button onclick={testBtn}>Test</button>