import client, { request, toOperatorError } from './client';

function fileNameFromDisposition(value) {
  const utf8 = value?.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
  if (utf8) return decodeURIComponent(utf8);
  return value?.match(/filename="?([^";]+)"?/i)?.[1] || 'kairon-data.db';
}

export async function downloadData() {
  try {
    const response = await client.get('/v1/data/export', { responseType: 'blob' });
    return {
      blob: response.data,
      fileName: fileNameFromDisposition(response.headers?.['content-disposition'])
    };
  } catch (error) {
    throw toOperatorError(error);
  }
}

export function deleteAllData(confirmation) {
  return request(client.delete('/v1/data', { data: { confirmation } }));
}
