/**
 * Android Command Relay — queues commands from Android, lets Core poll + respond.
 * Commands flow: Android → POST /v1/android/command → Core polls → Core posts result
 */
import { randomUUID } from 'crypto';

export type CommandType = 'TASK' | 'GOAL' | 'CHAT' | 'STATUS' | 'APPROVE' | 'DENY';
export type CommandStatus = 'PENDING' | 'RUNNING' | 'DONE' | 'FAILED';

export interface AndroidCommand {
  id: string;
  type: CommandType;
  payload: Record<string, unknown>;
  deviceId: string;
  createdAt: string;
  status: CommandStatus;
  result?: Record<string, unknown>;
  completedAt?: string;
}

const commands = new Map<string, AndroidCommand>();

export function createCommand(
  type: CommandType,
  payload: Record<string, unknown>,
  deviceId: string
): AndroidCommand {
  const cmd: AndroidCommand = {
    id: randomUUID(),
    type,
    payload,
    deviceId,
    createdAt: new Date().toISOString(),
    status: 'PENDING',
  };
  commands.set(cmd.id, cmd);
  console.log(`[Commands] Created command ${cmd.id} type=${type} device=${deviceId}`);
  return cmd;
}

export function getPendingCommands(): AndroidCommand[] {
  return Array.from(commands.values()).filter(c => c.status === 'PENDING');
}

export function updateCommand(
  id: string,
  status: CommandStatus,
  result?: Record<string, unknown>
): AndroidCommand | null {
  const cmd = commands.get(id);
  if (!cmd) return null;
  cmd.status = status;
  if (result !== undefined) cmd.result = result;
  if (status === 'DONE' || status === 'FAILED') cmd.completedAt = new Date().toISOString();
  return cmd;
}

export function getCommand(id: string): AndroidCommand | undefined {
  return commands.get(id);
}

export function getAllCommands(): AndroidCommand[] {
  return Array.from(commands.values());
}

/** Remove completed/failed commands older than 1 hour */
export function purgeOldCommands(): number {
  const cutoff = Date.now() - 60 * 60 * 1000;
  let count = 0;
  for (const [id, cmd] of commands) {
    if (
      (cmd.status === 'DONE' || cmd.status === 'FAILED') &&
      new Date(cmd.createdAt).getTime() < cutoff
    ) {
      commands.delete(id);
      count++;
    }
  }
  return count;
}
