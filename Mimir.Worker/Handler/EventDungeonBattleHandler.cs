using Lib9c.Abstractions;
using Libplanet.Action;
using Mimir.Worker.CollectionUpdaters;
using Mimir.Worker.Services;
using Nekoyume.Model.EnumType;
using Serilog;

namespace Mimir.Worker.Handler;

public class EventDungeonBattleHandler(IStateService stateService, MongoDbService store) :
    BaseActionHandler(
        stateService,
        store,
        "^event_dungeon_battle[0-9]*$",
        Log.ForContext<EventDungeonBattleHandler>())
{
    protected override async Task HandleAction(long blockIndex, IAction action)
    {
        if (action is not IEventDungeonBattleV2 eventDungeonBattle)
        {
            throw new NotImplementedException(
                $"Action is not {nameof(IEventDungeonBattleV2)}: {action.GetType()}");
        }

        await ItemSlotCollectionUpdater.UpdateAsync(
            Store,
            BattleType.Adventure,
            eventDungeonBattle.AvatarAddress,
            eventDungeonBattle.Costumes,
            eventDungeonBattle.Equipments);
    }
}
