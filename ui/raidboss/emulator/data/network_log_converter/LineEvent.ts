import { Job } from '../../../../../types/job';
import EmulatorCommon, { getTimezoneOffsetMillis } from '../../EmulatorCommon';

import LogRepository from './LogRepository';

const fields = {
  event: 0,
  timestamp: 1,
} as const;

const unknownLogMessagePrefix = 'Unknown';

const logMessagePrefix: { [decEvent: number]: string } = {
  0x00: 'ChatLog',
  0x01: 'Territory',
  0x02: 'ChangePrimaryPlayer',
  0x03: 'AddCombatant',
  0x04: 'RemoveCombatant',
  0x0b: 'PartyList',
  0x0c: 'PlayerStats',
  0x14: 'StartsCasting',
  0x15: 'ActionEffect',
  0x16: 'AOEActionEffect',
  0x17: 'CancelAction',
  0x18: 'DoTHoT',
  0x19: 'Death',
  0x1a: 'StatusAdd',
  0x1b: 'TargetIcon',
  0x1c: 'WaymarkMarker',
  0x1d: 'SignMarker',
  0x1e: 'StatusRemove',
  0x1f: 'Gauge',
  0x20: 'World',
  0x21: 'Director',
  0x22: 'NameToggle',
  0x23: 'Tether',
  0x24: 'LimitBreak',
  0x25: 'EffectResult',
  0x26: 'StatusList',
  0x27: 'UpdateHp',
  0x28: 'ChangeMap',
  0x29: 'SystemLogMessage',
  0xf9: 'Settings',
  0xfa: 'Process',
  0xfb: 'Debug',
  0xfc: 'PacketDump',
  0xfd: 'Version',
  0xfe: 'Error',
};

/**
 * Generic class to track an FFXIV log line
 */
export default class LineEvent {
  public offset = 0;
  public convertedLine: string;
  public invalid = false;
  public index = 0;
  public readonly decEvent: number;
  public readonly hexEvent: string;
  public readonly timestamp: number;
  public readonly checksum: string;
  public readonly tzOffsetMillis: number;

  constructor(repo: LogRepository, public networkLine: string, parts: string[]) {
    const timestampString = parts[fields.timestamp] ?? '0';
    this.tzOffsetMillis = getTimezoneOffsetMillis(timestampString);
    this.decEvent = parseInt(parts[fields.event] ?? '0');
    this.hexEvent = EmulatorCommon.zeroPad(this.decEvent.toString(16).toUpperCase());
    this.timestamp = new Date(timestampString).getTime();
    this.checksum = parts.slice(-1)[0] ?? '';
    repo.updateTimestamp(this.timestamp);
    this.convertedLine = this.prefix() + (parts.slice(2, -1).join(':')).replace('|', ':');
  }

  prefix(): string {
    const timeString = EmulatorCommon.timeToTimeString(this.timestamp, this.tzOffsetMillis, true);
    const logMessageName = logMessagePrefix[this.decEvent] ?? unknownLogMessagePrefix;
    return `[${timeString}] ${logMessageName} ${this.hexEvent}:`;
  }

  static isDamageHallowed(damage: string): boolean {
    return (parseInt(damage, 16) & parseInt('1000', 16)) > 0;
  }

  static isDamageBig(damage: string): boolean {
    return (parseInt(damage, 16) & parseInt('4000', 16)) > 0;
  }

  static calculateDamage(damage: string): number {
    if (LineEvent.isDamageHallowed(damage))
      return 0;

    damage = EmulatorCommon.zeroPad(damage, 8);
    const parts = [
      damage.substr(0, 2),
      damage.substr(2, 2),
      damage.substr(4, 2),
      damage.substr(6, 2),
    ] as const;

    if (!LineEvent.isDamageBig(damage))
      return parseInt(parts.slice(0, 2).reverse().join(''), 16);

    return parseInt(
      (parts[3] + parts[0]) +
        (parseInt(parts[1], 16) - parseInt(parts[3], 16)).toString(16),
      16,
    );
  }
}

// Type guards for these interfaces require their own descriptor property
// because we don't want every line event with an id/name
// to update combatant state, for example
export interface LineEventSource extends LineEvent {
  readonly isSource: true;
  readonly id: string;
  readonly name: string;
  readonly x?: number;
  readonly y?: number;
  readonly z?: number;
  readonly heading?: number;
  readonly targetable?: boolean;
  readonly hp?: number;
  readonly maxHp?: number;
  readonly mp?: number;
  readonly maxMp?: number;
}

export const isLineEventSource = (line: LineEvent): line is LineEventSource => {
  return 'isSource' in line;
};

export interface LineEventTarget extends LineEvent {
  readonly isTarget: true;
  readonly targetId: string;
  readonly targetName: string;
  readonly targetX?: number;
  readonly targetY?: number;
  readonly targetZ?: number;
  readonly targetHeading?: number;
  readonly targetHp?: number;
  readonly targetMaxHp?: number;
  readonly targetMp?: number;
  readonly targetMaxMp?: number;
}

export const isLineEventTarget = (line: LineEvent): line is LineEventTarget => {
  return 'isTarget' in line;
};

export interface LineEventJobLevel extends LineEvent {
  readonly isJobLevel: true;
  readonly job: Job;
  readonly jobId: number;
  readonly level: number;
}

export const isLineEventJobLevel = (line: LineEvent): line is LineEventJobLevel => {
  return 'isJobLevel' in line;
};

export interface LineEventAbility extends LineEvent {
  readonly isAbility: true;
  readonly abilityId: number;
  readonly abilityName: string;
}

export const isLineEventAbility = (line: LineEvent): line is LineEventAbility => {
  return 'isAbility' in line;
};
