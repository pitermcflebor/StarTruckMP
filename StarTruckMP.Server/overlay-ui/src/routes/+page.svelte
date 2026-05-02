<script lang="ts">
    import {type GameMessage, onGameMessage, sendToGame} from "$lib/gameEvents";
    import ObjectInspector from "$lib/inspector/ObjectInspector.svelte";
    import RunCode from "$lib/runcode/RunCode.svelte";
    
    sendToGame("overlayLoaded", { timestamp: Date.now() });
</script>

<div class="container">
    <ObjectInspector />
    <!--<RunCode />-->
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