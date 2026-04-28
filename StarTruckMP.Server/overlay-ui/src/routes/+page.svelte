<script lang="ts">
    import {type GameMessage, onGameMessage, sendToGame} from "$lib/gameEvents";
    import ObjectInspector from "$lib/inspector/ObjectInspector.svelte";
    
    let lastMessage = $state<GameMessage>({} as GameMessage);
    
    onGameMessage((message => {
        lastMessage = message;
    }));
    
    sendToGame("overlayLoaded", { timestamp: Date.now() });
    
    function testBtn() {
        sendToGame("testButtonClicked", { timestamp: Date.now() });
    }
</script>

<div class="container">
    <ObjectInspector />
</div>

<style>
    .container {
        position: absolute;
        top: 0;
        left: 0;
        width: 100%;
        height: 100%;
    }
</style>