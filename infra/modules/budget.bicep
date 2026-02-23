module budget './modules/budget.bicep' = {
  name: 'budget'
  params: {
    amount: environment == 'prod' ? 1000 : 200
  }
}
